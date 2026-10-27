using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZephyrPlayer
{
    /// <summary>
    /// The one NEW PC-side endpoint (protocol.md Handoff): the paired phone posts
    /// POST /play {"url":<stream-url>,"position":<sec>?,"subtitles":[...]} and the
    /// player opens it in libVLC. Own port + firewall rule (independent identity -
    /// no shared port with the BingeMote helper, no stand-down).
    /// Pairing/auth: the phone proves itself with its pairing token in an
    /// Authorization header; the per-session stream token lives in the URL path.
    /// Skeleton: libVLC wiring lands with the airspace prototype validation.
    /// </summary>
    public sealed class PlaybackServer : IDisposable
    {
        public const int DefaultPort = 47823; // distinct from BingeMote helper's port
        private readonly HttpListener _listener = new HttpListener();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Func<string, long?, string[], Task> _onPlay;
        private readonly Func<string, bool> _validateToken;
        private readonly Func<string, (PairOutcome outcome, string? token)>? _onPair;
        private readonly PlayerSettings _settings;
        private readonly IInputSink? _input;
        private readonly IPlaybackControl? _control;
        private readonly bool _loopbackOnly;

        private static readonly System.Collections.Generic.HashSet<string> NamedKeys = new()
            { "enter", "backspace", "escape", "space", "tab", "up", "down", "left", "right" };
        public int Port { get; }

        /// <param name="validateToken">Checks a Bearer token against the paired-phone
        /// store. Auth is MANDATORY: no build that opens this listener ships or runs
        /// in tests without it.</param>
        /// <param name="port">Listening port; tests pass an explicit loopback port.</param>
        /// <param name="loopbackOnly">Bind 127.0.0.1 only (CI harness); production binds all interfaces.</param>
        /// <param name="onPair">6-digit code in, (outcome, token) out - the PairingGate
        /// owns code lifetime, one-shot spend, lockout and the phone cap. Optional so the
        /// loopback harness can run without pairing; production wires it.</param>
        public PlaybackServer(Func<string, long?, string[], Task> onPlay, Func<string, bool> validateToken,
            int port = DefaultPort, bool loopbackOnly = false,
            Func<string, (PairOutcome outcome, string? token)>? onPair = null,
            PlayerSettings? settings = null,
            IInputSink? input = null,
            IPlaybackControl? control = null)
        {
            _onPlay = onPlay;
            _validateToken = validateToken ?? throw new ArgumentNullException(nameof(validateToken));
            _onPair = onPair;
            _settings = settings ?? new PlayerSettings();
            _input = input;
            _control = control;
            _loopbackOnly = loopbackOnly;
            Port = port;
            _listener.Prefixes.Add(loopbackOnly ? $"http://127.0.0.1:{port}/" : $"http://+:{port}/");
        }

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            try
            {
                // LAN-only, inherited from the helper model: refuse anything that is not
                // loopback / RFC1918 / link-local before touching auth or routes.
                if (!_loopbackOnly && !IsPrivateAddress(ctx.Request.RemoteEndPoint.Address))
                {
                    await Reply(ctx, 403, "{\"error\":\"not a home network address\"}");
                    return;
                }
                // Pairing is the ONE unauthenticated route: the phone trades the 6-digit
                // code on the player screen for a pairing token (protocol.md Pairing).
                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/pair")
                {
                    using var pairReader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var pairBody = await pairReader.ReadToEndAsync();
                    var code = System.Text.Json.JsonDocument.Parse(pairBody).RootElement.GetProperty("code").GetString() ?? "";
                    var (outcome, newToken) = code.Length == 0 || _onPair == null
                        ? (PairOutcome.NoCode, null)
                        : _onPair(code);
                    switch (outcome)
                    {
                        case PairOutcome.Ok:
                            await Reply(ctx, 200, "{\"token\":\"" + newToken + "\"}");
                            return;
                        case PairOutcome.Locked:
                            // Time-based lock: the phone says "try again in a few
                            // minutes", never "restart the PC" (1.2.0).
                            await Reply(ctx, 429, "{\"error\":\"locked\",\"detail\":\"Too many wrong codes. Try again in a few minutes.\"}");
                            return;
                        case PairOutcome.TooManyPhones:
                            await Reply(ctx, 409, "{\"error\":\"too many phones\"}");
                            return;
                        case PairOutcome.CodeExpired:
                            // The code died on screen: distinct from a WRONG code so
                            // the phone can say "use the code showing on the PC now".
                            await Reply(ctx, 410, "{\"error\":\"code expired\",\"detail\":\"That code expired. Use the code showing on the PC screen now.\"}");
                            return;
                        case PairOutcome.NoCode:
                            await Reply(ctx, 403, "{\"error\":\"no code\",\"detail\":\"No code is showing on the PC. Open pairing on the PC first.\"}");
                            return;
                        default: // BadCode
                            await Reply(ctx, 403, "{\"error\":\"bad code\",\"detail\":\"Wrong code - check the code on the PC screen.\"}");
                            return;
                    }
                }
                var auth = ctx.Request.Headers["Authorization"] ?? "";
                var token = auth.StartsWith("Bearer ") ? auth.Substring(7) : "";
                if (token.Length == 0 || !_validateToken(token))
                {
                    await Reply(ctx, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }
                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/play")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var doc = System.Text.Json.JsonDocument.Parse(body).RootElement;
                    var url = doc.GetProperty("url").GetString();
                    long? position = doc.TryGetProperty("position", out var p) ? p.GetInt64() : null;
                    var subs = doc.TryGetProperty("subtitles", out var s)
                        ? System.Text.Json.JsonSerializer.Deserialize<string[]>(s.GetRawText()) ?? Array.Empty<string>()
                        : Array.Empty<string>();
                    await _onPlay(url, position, subs);
                    await Reply(ctx, 200, "{\"ok\":true}");
                    return;
                }
                // Library browsing (protocol.md Library): the paired phone picks the
                // assigned movies folder and lists it, same contract as the helper's
                // /movies/list + /browse. All read-only except POST /library/folder.
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/library/folder")
                {
                    await Reply(ctx, 200, System.Text.Json.JsonSerializer.Serialize(new { libraryFolder = _settings.LibraryFolder }));
                    return;
                }
                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/library/folder")
                {
                    using var folderReader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var folderBody = await folderReader.ReadToEndAsync();
                    var folder = System.Text.Json.JsonDocument.Parse(folderBody).RootElement.GetProperty("folder").GetString()?.Trim() ?? "";
                    if (folder.Length > 0)
                    {
                        var check = Library.Browse(folder);
                        if (check.status != 200) { await Reply(ctx, 400, check.json); return; }
                    }
                    _settings.LibraryFolder = folder;
                    await Reply(ctx, 200, System.Text.Json.JsonSerializer.Serialize(new { libraryFolder = folder }));
                    return;
                }
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/library/list")
                {
                    var (status, json) = Library.List(_settings.LibraryFolder, ctx.Request.QueryString["path"] ?? "");
                    await Reply(ctx, status, json);
                    return;
                }
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/library/browse")
                {
                    var (status, json) = Library.Browse(ctx.Request.QueryString["path"] ?? "");
                    await Reply(ctx, status, json);
                    return;
                }
                // Playback control (protocol.md Control): the paired phone drives the
                // player's libVLC. 404 when no control seam is wired.
                if (ctx.Request.Url.AbsolutePath.StartsWith("/control/"))
                {
                    if (_control == null) { await Reply(ctx, 404, "{}"); return; }
                    if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/control/status")
                    {
                        await Reply(ctx, 200, _control.StatusJson());
                        return;
                    }
                    if (ctx.Request.HttpMethod != "POST") { await Reply(ctx, 404, "{}"); return; }
                    System.Text.Json.JsonElement cjson = default;
                    var hasBody = ctx.Request.HasEntityBody;
                    if (hasBody)
                    {
                        using var controlReader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        var controlBody = await controlReader.ReadToEndAsync();
                        cjson = System.Text.Json.JsonDocument.Parse(controlBody).RootElement;
                    }
                    switch (ctx.Request.Url.AbsolutePath)
                    {
                        case "/control/play":
                        {
                            var url = cjson.GetProperty("url").GetString() ?? "";
                            long? pos = cjson.TryGetProperty("position", out var cp) ? cp.GetInt64() : null;
                            var subs = cjson.TryGetProperty("subtitles", out var cs)
                                ? System.Text.Json.JsonSerializer.Deserialize<string[]>(cs.GetRawText()) ?? Array.Empty<string>()
                                : Array.Empty<string>();
                            if (url.Length == 0) { await Reply(ctx, 400, "{\"error\":\"no url\"}"); return; }
                            _control.Play(url, pos, subs);
                            break;
                        }
                        case "/control/pause": _control.Pause(); break;
                        case "/control/resume": _control.Resume(); break;
                        case "/control/seek":
                            _control.Seek(cjson.GetProperty("position").GetInt64());
                            break;
                        case "/control/volume":
                            var vol = cjson.GetProperty("volume").GetInt32();
                            if (vol < 0 || vol > 100) { await Reply(ctx, 400, "{\"error\":\"volume 0-100\"}"); return; }
                            _control.SetVolume(vol);
                            break;
                        case "/control/mute": _control.Mute(cjson.GetProperty("mute").GetBoolean()); break;
                        case "/control/captions": _control.SetSpu(cjson.GetProperty("track").GetInt32()); break;
                        case "/control/fullscreen": _control.SetFullscreen(cjson.GetProperty("on").GetBoolean()); break;
                        default:
                            await Reply(ctx, 404, "{}");
                            return;
                    }
                    await Reply(ctx, 200, "{\"ok\":true}");
                    return;
                }
                // Input receiver (protocol.md Input, HTTP POST fallback set): mouse /
                // keyboard replay into Windows via the injected sink (SendInput in
                // production, a recorder in tests). 404 when no sink is wired.
                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath.StartsWith("/input/"))
                {
                    if (_input == null) { await Reply(ctx, 404, "{}"); return; }
                    using var inputReader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var inputBody = await inputReader.ReadToEndAsync();
                    var json = System.Text.Json.JsonDocument.Parse(inputBody).RootElement;
                    switch (ctx.Request.Url.AbsolutePath)
                    {
                        case "/input/move":
                            _input.Move(json.GetProperty("dx").GetSingle(), json.GetProperty("dy").GetSingle());
                            break;
                        case "/input/button":
                            var button = json.GetProperty("button").GetString() ?? "";
                            if (button != "left" && button != "right")
                            {
                                await Reply(ctx, 400, "{\"error\":\"unknown button\"}");
                                return;
                            }
                            _input.Button(button, json.GetProperty("down").GetBoolean());
                            break;
                        case "/input/scroll":
                            _input.Scroll(json.GetProperty("dy").GetSingle());
                            break;
                        case "/input/zoom":
                            _input.Zoom(json.GetProperty("delta").GetSingle());
                            break;
                        case "/input/key":
                            var key = json.GetProperty("key").GetString() ?? "";
                            if (!NamedKeys.Contains(key))
                            {
                                await Reply(ctx, 400, "{\"error\":\"unknown key\"}");
                                return;
                            }
                            _input.NamedKey(key);
                            break;
                        case "/input/type":
                            _input.TypeText(json.GetProperty("text").GetString() ?? "");
                            break;
                        default:
                            await Reply(ctx, 404, "{}");
                            return;
                    }
                    await Reply(ctx, 200, "{\"ok\":true}");
                    return;
                }
                await Reply(ctx, 404, "{}");
            }
            catch (Exception e)
            {
                await Reply(ctx, 500, "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(e.Message) + "}");
            }
        }

        private static async Task Reply(HttpListenerContext ctx, int code, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        /// <summary>Loopback or RFC1918 / link-local: a home network, not the internet.
        /// Same rule as the BingeMote helper's HttpServer.IsPrivateAddress.</summary>
        internal static bool IsPrivateAddress(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }
}
