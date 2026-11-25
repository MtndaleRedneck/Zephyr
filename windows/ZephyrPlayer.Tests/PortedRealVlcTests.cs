using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;

namespace ZephyrPlayer.Tests;

// Four 2.0.2 behavior tests (Cp1RepairTests) that the 3.0 rebuild dropped, ported onto the current MainWindow seams with
// the same assertions: a slow source is not stopped, Playing alone keeps the spinner until buffering completes, an old
// start's late failure never touches a newer movie, and a Boost change reaches the playing movie's decoder gain.
// These use real libVLC and run only in CI; nothing here has been compiled or run yet.
public class PortedRealVlcTests
{
    private static string ClipUrl => new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4")).AbsoluteUri;

    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + ms;
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        } while (Environment.TickCount64 < until);
    }

    private static void PumpUntil(Func<bool> test, string message, int ms = 8000)
    {
        var until = Environment.TickCount64 + ms;
        while (!test() && Environment.TickCount64 < until) Pump(20);
        Assert.True(test(), message);
    }

    // 2.0.2: a source that keeps delivering, only slowly, is NOT stopped by the stall window: it plays.
    [Fact]
    public void SlowPhoneSource_KeepsDelivering_IsNotStopped_AndPlays()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            using var slow = new DribbleServer(slowSeconds: 12);
            w.NativeStallWindow = TimeSpan.FromSeconds(2);
            var t0 = Environment.TickCount64;
            var start = w.PlayFromUrlAsync(slow.Url, null, Array.Empty<string>());
            PumpUntil(() => start.IsCompleted, "slow start never handed off", 20000);
            start.GetAwaiter().GetResult();
            PumpUntil(() => (w.PlayerStateForTest == VLCState.Playing && !w.IsLoadingShown) || w.IsLoadingErrorShown, "slow start neither played nor failed", 40000);
            var elapsed = Environment.TickCount64 - t0;
            Assert.False(w.IsLoadingErrorShown, "a slow but valid source was stopped: " + w.LastStartFailure);
            Assert.Null(w.LastStartFailure);
            Assert.Equal(VLCState.Playing, w.PlayerStateForTest);
            // libVLC reports Playing once the demuxer opens; the assertion that the source really was slow is the elapsed time.
            Assert.True(elapsed > 3 * 2000, $"the source was not actually slow ({elapsed} ms)");
            Assert.True(slow.Requests > 0);
            w.StopPlaybackSource();
        }, joinMs: 75000, mustFinish: true);
    }

    // 2.0.2: libVLC's Playing (or a black output) alone keeps the spinner; buffering complete after Playing ends it.
    [Fact]
    public void Playing_AloneKeepsSpinner_UntilBufferingCompletes()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            using var slow = new DribbleServer(slowSeconds: 15);
            var start = w.PlayFromUrlAsync(slow.Url, null, Array.Empty<string>());
            PumpUntil(() => start.IsCompleted, "slow start never handed off", 20000);
            start.GetAwaiter().GetResult();
            var a = w.CurrentNativeStart!;
            Assert.True(a.Waiting);
            w.RaiseNativeEvent(a, "playing");
            Pump(400);
            Assert.True(a.PlayingSeen);
            Assert.True(a.Waiting, "Playing alone ended the start before any picture");
            Assert.True(w.IsLoadingShown, "Playing alone hid the spinner");
            w.RaiseNativeBuffering(a, 100f);
            Pump(400);
            Assert.False(a.Waiting);
            Assert.Equal("buffered", a.CompletedBy);
            Assert.False(w.IsLoadingShown, "buffering complete after Playing did not end loading");
            Assert.Null(w.LastStartFailure);
            w.StopPlaybackSource();
        }, joinMs: 45000, mustFinish: true);
    }

    // 2.0.2: the old request ignores cancellation and fails for real AFTER the newer start is playing and paused.
    [Fact]
    public void SupersededStart_OldFailure_NeverTouchesNewerStart()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            const string oldUrl = "http://127.0.0.1:9/s/old/1";
            using var gate = new ManualResetEventSlim(false);
            w.PrepareOverride = (url, subs, token) =>
            {
                if (url != oldUrl) return w.PrepareSource(url, subs, token);
                gate.Wait(20000);
                throw new InvalidDataException("old phone request failed late");
            };
            var old = w.PlayFromUrlAsync(oldUrl, null, Array.Empty<string>());
            var ping = false; w.Dispatcher.BeginInvoke(new Action(() => ping = true));
            PumpUntil(() => ping, "preparation blocked the UI");
            var next = w.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
            PumpUntil(() => next.IsCompleted, "newer start did not complete", 15000);
            next.GetAwaiter().GetResult();
            PumpUntil(() => w.PlayerStateForTest == VLCState.Playing, "newer start did not play", 15000);
            PumpUntil(() => !w.IsLoadingShown, "newer start left loading up", 15000);
            Assert.True(w.PauseFromPhone(true));
            PumpUntil(() => w.PlayerStateForTest == VLCState.Paused, "newer start did not pause");
            var media = w.CurrentMedia; var generation = w.PlayGeneration; var title = w.CurrentMovieTitle;
            Assert.NotNull(media);

            gate.Set();
            PumpUntil(() => old.IsCompleted, "old start never completed");
            Assert.True(old.IsFaulted);
            Assert.IsType<InvalidDataException>(old.Exception!.GetBaseException());
            Pump(500);
            Assert.False(w.IsLoadingShown, "old failure re-opened the loading/error panel over the newer movie");
            Assert.Null(w.LastStartFailure);
            Assert.Equal("clip", title);
            Assert.Equal(title, w.CurrentMovieTitle);
            Assert.Same(media, w.CurrentMedia);
            Assert.Equal(generation, w.PlayGeneration);
            Assert.Equal(VLCState.Paused, w.PlayerStateForTest);
            w.StopPlaybackSource();
        }, joinMs: 60000, mustFinish: true);
    }

    // 2.0.2: Boost (Settings) reaches the playing movie's decoder gain at once, with no reopen, and leaves the slider alone.
    [Fact]
    public void BoostChange_WhilePlaying_ReachesDecoderGain_WithoutReopen()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            settings.VolumeBoostMaxPct = 100;
            var start = w.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
            PumpUntil(() => start.IsCompleted, "local start did not complete", 15000);
            start.GetAwaiter().GetResult();
            PumpUntil(() => w.PlayerStateForTest == VLCState.Playing, "clip did not reach Playing", 15000);

            w.SetVolumeTied(50);
            Assert.Equal(50, w.AppliedPlayerGain);          // boost 100 %: gain = slider
            var media = w.CurrentMedia;
            var generation = w.PlayGeneration;
            Assert.NotNull(media);

            settings.VolumeBoostMaxPct = 200;               // what the Settings Boost choice writes
            PumpUntil(() => w.AppliedPlayerGain == 100, "Boost 200 % did not reach the decoder gain");
            settings.VolumeBoostMaxPct = 150;
            PumpUntil(() => w.AppliedPlayerGain == 75, "Boost 150 % did not reach the decoder gain");

            Assert.Same(media, w.CurrentMedia);
            Assert.Equal(generation, w.PlayGeneration);
            Assert.Equal(50, w.SharedVolume);
            w.StopPlaybackSource();
        }, joinMs: 45000, mustFinish: true);
    }

    // The 2.0.2 DribbleServer: serves clip.mp4 in 256-byte pieces with a pause for slowSeconds after the first request,
    // then at full speed. Same code as the 2.0.2 test class.
    private sealed class DribbleServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4"));
        private readonly CancellationTokenSource _stop = new();
        private long _firstRequest = -1, _served; private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string Url { get; }
        public DribbleServer(int slowSeconds)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://127.0.0.1:{port}/s/tok/1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { break; }
                    _ = Task.Run(() => Serve(ctx, slowSeconds));
                }
            });
        }
        private async Task Serve(HttpListenerContext ctx, int slowSeconds)
        {
            Interlocked.Increment(ref _requests);
            Interlocked.CompareExchange(ref _firstRequest, Environment.TickCount64, -1);
            try
            {
                long start = 0, end = _file.Length - 1;
                var range = ctx.Request.Headers["Range"];
                var m = range == null ? null : System.Text.RegularExpressions.Regex.Match(range, @"bytes=(\d+)-(\d*)");
                if (m != null && m.Success) { start = long.Parse(m.Groups[1].Value); if (m.Groups[2].Value.Length > 0) end = Math.Min(end, long.Parse(m.Groups[2].Value)); }
                if (start > end) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }
                ctx.Response.StatusCode = m != null && m.Success ? 206 : 200;
                ctx.Response.ContentType = "video/mp4";
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                if (ctx.Response.StatusCode == 206) ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{_file.Length}";
                ctx.Response.ContentLength64 = end - start + 1;
                ctx.Response.SendChunked = false;
                var output = ctx.Response.OutputStream;
                var pos = start;
                while (pos <= end && !_stop.IsCancellationRequested)
                {
                    var slowPhase = Environment.TickCount64 - Interlocked.Read(ref _firstRequest) < slowSeconds * 1000L;
                    var n = (int)Math.Min(end - pos + 1, slowPhase ? 256 : 64 * 1024);
                    await output.WriteAsync(_file.AsMemory((int)pos, n), _stop.Token);
                    await output.FlushAsync(_stop.Token);
                    pos += n; Interlocked.Add(ref _served, n);
                    if (slowPhase) await Task.Delay(250, _stop.Token);
                }
            }
            catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        }
        public void Dispose() { _stop.Cancel(); try { _listener.Close(); } catch { } }
    }
}
