// Headless CI host: runs the REAL PlaybackServer + Library + PairingGate on
// Linux so the Android instrumented tests exercise the actual phone<->player
// wire protocol (no mocks). Stubs record input/control calls; the play stub
// even fetches the stream URL back (phone->player streaming round trip).
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZephyrPlayer;

var port = args.Length > 0 ? int.Parse(args[0]) : 47999;
var library = args.Length > 1 ? args[1] : "/tmp/zephyr-wire-lib";

var gate = new PairingGate();
var tokens = new HashSet<string>();
var code = gate.NewCode();
// The harness greps this line and passes the code to the Android test.
Console.WriteLine($"PAIRCODE {code}");

var inputs = new List<string>();
var stub = new StubControl();

using var server = new PlaybackServer(
    onPlay: (url, pos, subs) => stub.PlayAsync(url, pos, subs),
    validateToken: tokens.Contains,
    port: port,
    onPair: tryCode =>
    {
        var outcome = gate.TryPair(tryCode, tokens.Count, () =>
        {
            var t = Guid.NewGuid().ToString("N");
            lock (tokens) tokens.Add(t);
            return t;
        }, out var token);
        return (outcome, token);
    },
    settings: new PlayerSettings("/tmp/zephyr-wire-settings.json") { LibraryFolder = library },
    input: new StubInput(inputs),
    control: stub);
server.Start();
Console.WriteLine($"LISTENING {server.Port}");
await Task.Delay(Timeout.Infinite);

internal sealed class StubInput : IInputSink
{
    private readonly List<string> _log;
    public StubInput(List<string> log) { _log = log; }
    private void Rec(string s) { lock (_log) _log.Add(s); }
    public void Move(float dx, float dy) => Rec($"move {dx} {dy}");
    public void Button(string button, bool down) => Rec($"button {button} {down}");
    public void Scroll(float dy) => Rec($"scroll {dy}");
    public void Zoom(float delta) => Rec($"zoom {delta}");
    public void NamedKey(string key) => Rec($"key {key}");
    public void TypeText(string text) => Rec($"type {text}");
}

internal sealed class StubControl : IPlaybackControl
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    private string _lastPlay = "none";
    public string LastError = "";

    public async Task PlayAsync(string url, long? pos, string[] subs)
    {
        Play(url, pos, subs);
        // Phone->player stream handoff, for real: fetch what the phone serves.
        // adb reverse maps this host's 127.0.0.1:<port> to the emulator.
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var full = await Http.GetAsync(url);
                var bytes = (await full.Content.ReadAsByteArrayAsync()).Length;
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Range", "bytes=0-99");
                using var range = await Http.SendAsync(req);
                _lastPlay = $"fetched:{(int)full.StatusCode}:{bytes}:range:{(int)range.StatusCode}";
            }
            catch (Exception e) { _lastPlay = "fetch-failed"; LastError = e.Message; }
        }
        else _lastPlay = "local:" + url;
    }

    public void Play(string url, long? positionSeconds, string[] subtitles)
        { _lastPlay = "pending:" + url; }
    public void Pause() { }
    public void Resume() { }
    public void Seek(long positionSeconds) { }
    public void SetVolume(int volume) { }
    public void Mute(bool mute) { }
    public void SetSpu(int trackId) { }
    public void SetFullscreen(bool on) { }
    public string StatusJson() =>
        "{\"state\":\"Playing\",\"time\":42,\"length\":3600,\"volume\":80," +
        "\"muted\":false,\"spu\":-1,\"fullscreen\":false,\"update_available\":null," +
        "\"update_check\":\"up_to_date\",\"last_play\":\"" + _lastPlay + "\"}";
}
