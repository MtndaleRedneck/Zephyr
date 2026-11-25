using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

namespace ZephyrPlayer.Tests;

// Carried from 2.0.2 (Cp1Repair, Cp2Player): a failed start ends loading and shows the error card, Try again re-runs the
// same start, the Opening state shows the new movie and never an old failure, phone play/pause/seek never reach the
// old movie while a start opens, a superseded start never touches a newer one, a stalled start fails.
public class StartLifecycleTests
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

    // Same wait and same assertion; the message is built when the wait ends, so it can carry the state at failure.
    private static void PumpUntil(Func<bool> test, Func<string> message, int ms = 8000)
    {
        var until = Environment.TickCount64 + ms;
        while (!test() && Environment.TickCount64 < until) Pump(20);
        var ok = test(); // one final evaluation, as the string overload does
        Assert.True(ok, ok ? "" : message());
    }

    private static void Await(Task t, int ms = 8000) { PumpUntil(() => t.IsCompleted, "the start did not finish", ms); }

    [Fact]
    public void FailedPreparation_ShowsErrorCard_TryAgainRerunsTheSameStart()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            var calls = 0;
            w.PrepareOverride = (url, subs, token) => { calls++; throw new InvalidDataException("bad"); };
            var sub = new[] { "http://127.0.0.1:1/s/a/1" };
            var t = w.PlayFromUrlAsync(ClipUrl, 42, sub);
            Await(t);
            Assert.True(t.IsFaulted);
            Assert.True(w.IsLoadingErrorShown);
            Assert.Equal(MainWindow.OpenFailedMessage, w.LoadingDetailText);
            Assert.Equal("preparation", w.LastStartFailure);
            Assert.Equal(MainWindow.OpenFailedMessage, w.CurrentMovieTitle);                      // phone status keeps CP1's behaviour
            Assert.NotEqual(MainWindow.OpenFailedMessage, ((System.Windows.Controls.TextBlock)w.FindName("MovieTitle")!).Text);   // never written into the title
            var before = w.LastStartArgs!.Value;
            var retry = w.RetryLastStartAsync();
            Await(retry);
            Assert.Equal(1, w.RetryCount);
            Assert.Equal(2, calls);
            Assert.Equal(before.Url, w.LastStartArgs!.Value.Url);
            Assert.Equal(before.Position, w.LastStartArgs!.Value.Position);
            Assert.Equal(before.Subtitles, w.LastStartArgs!.Value.Subtitles);
        });
    }

    [Fact]
    public void Opening_ShowsTheNewTitle_EmptyTimeline_AndHidesAnOldFailure()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            w.PrepareOverride = (url, subs, token) => throw new InvalidDataException("bad");
            Await(w.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>()));
            Assert.True(w.IsLoadingErrorShown);
            var gate = new ManualResetEventSlim();
            w.PrepareOverride = (url, subs, token) => { gate.Wait(token); throw new OperationCanceledException(); };
            var second = new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mkv")).AbsoluteUri;
            var t = w.PlayFromUrlAsync(second, null, Array.Empty<string>());
            PumpUntil(() => !w.IsLoadingErrorShown, "the old failure stayed over the new start");
            Assert.True(w.IsLoadingShown);
            Assert.False(w.SeekBarEnabled);                         // the new start's timeline stays at zero
            Assert.Equal("clip", w.CurrentMovieTitle);
            Assert.Equal("0:00", ((System.Windows.Controls.TextBlock)w.FindName("ElapsedText")!).Text);
            Assert.False(w.PauseFromPhone(true));                    // nothing reaches the old movie while a start opens
            Assert.False(w.SeekFromPhone(30));
            Assert.Equal(1, w.PlayPauseRefusedCount);
            w.StopPlaybackSource();
            gate.Set();
            Pump(200);
        });
    }

    [Fact]
    public void NativeError_AndEndBeforePlaying_FailTheStart_WithTheNativeMessage()
    {
        // The start must still be WAITING when the error arrives (a real clip plays within ms on the runner and the
        // product correctly ignores an error after completion). The source sends its header and then holds.
        using var server = new StalledMovieServer();
        VolumeMuteTests.Sta((w, settings) =>
        {
            w.PrepareOverride = (url, subs, token) => new SafeMediaSource(w.LibVlcForTest, server.Url);
            // The start task completes once the prepared media is handed to libVLC (not on Playing), so the original
            // completion check stays; the held server keeps the native start Waiting after it.
            Await(w.PlayFromUrlAsync(server.Url, null, Array.Empty<string>()));
            var native = w.CurrentNativeStart;
            Assert.NotNull(native);
            w.RaiseNativeEvent(native!, "error");
            PumpUntil(() => w.IsLoadingErrorShown, () => $"a native error did not end loading (Waiting={native!.Waiting}, PlayingSeen={native.PlayingSeen}, CompletedBy={native.CompletedBy ?? "none"}, LastStartFailure={w.LastStartFailure ?? "none"})");
            Assert.Equal(MainWindow.NativeFailedMessage, w.LoadingDetailText);
            Assert.Equal("native-error", w.LastStartFailure);
            w.StopPlaybackSource();
        });
    }

    [Fact]
    public void StalePreviousStart_NativeEvents_NeverTouchANewerStart()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            w.PrepareOverride = (url, subs, token) => new SafeMediaSource(w.LibVlcForTest, url);
            Await(w.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>()));
            var first = w.CurrentNativeStart!;
            var second = new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mkv")).AbsoluteUri;
            Await(w.PlayFromUrlAsync(second, null, Array.Empty<string>()));
            var newer = w.CurrentNativeStart!;
            Assert.NotSame(first, newer);
            w.RaiseNativeEvent(first, "error");
            Pump(300);
            Assert.False(w.IsLoadingErrorShown, "an older start's failure showed over the newer start");
            Assert.True(newer.Waiting || newer.CompletedBy != null);
            w.StopPlaybackSource();
        });
    }

    [Fact]
    public void NoProgress_ForTheStallWindow_FailsTheStart_WithAnError()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            // A source nobody reads: header bytes, then nothing. No bytes move and libVLC never says Playing within the (shortened) window.
            using var server = new StalledMovieServer();
            try
            {
                w.NativeStallWindow = TimeSpan.FromMilliseconds(600);
                string? prepError = null;
                w.PrepareOverride = (url, subs, token) => { try { return new SafeMediaSource(w.LibVlcForTest, server.Url); } catch (Exception ex) { prepError = ex.GetType().Name + ": " + ex.Message; throw; } };
                Await(w.PlayFromUrlAsync(server.Url, null, Array.Empty<string>()));
                var native = w.CurrentNativeStart;
                Assert.NotNull(native);
                PumpUntil(() => w.IsLoadingErrorShown || w.LastStartFailure != null || native!.PlayingSeen, "neither a failure nor Playing arrived", 20000);
                // Unconditional: the held source never reaches Playing, and the failure is exactly the stall (never defaulted).
                var detail = $"LastStartFailure=[{w.LastStartFailure ?? "none"}], PlayingSeen={native!.PlayingSeen}; exception thrown while preparing: {prepError ?? "none"}; server requests: {server.Requests}";
                Assert.False(native.PlayingSeen, "the held source reached Playing: " + detail);
                Assert.True(w.LastStartFailure == "native-stalled", "expected native-stalled: " + detail);
            }
            finally { w.StopPlaybackSource(); }
        });
    }

    [Fact]
    public void StallWindow_Default_Is45Seconds()
    {
        VolumeMuteTests.Sta((w, settings) => Assert.Equal(TimeSpan.FromSeconds(45), w.NativeStallWindow));
    }
}
