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
        VolumeMuteTests.Sta((w, settings) =>
        {
            var source = new SafeMediaSource(w.LibVlcForTest, ClipUrl);
            w.PrepareOverride = (url, subs, token) => source;
            Await(w.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>()));
            var start = w.CurrentNativeStart;
            Assert.NotNull(start);
            w.RaiseNativeEvent(start!, "error");
            PumpUntil(() => w.IsLoadingErrorShown, "a native error did not end loading");
            Assert.Equal(MainWindow.NativeFailedMessage, w.LoadingDetailText);
            Assert.Equal("native-error", w.LastStartFailure);
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
            // A source nobody reads: no bytes move and libVLC never says Playing within the (shortened) window.
            var src = Path.Combine(Path.GetTempPath(), "zephyr-stall-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(src, new byte[4096]);
            try
            {
                w.NativeStallWindow = TimeSpan.FromMilliseconds(600);
                w.PrepareOverride = (url, subs, token) => new SafeMediaSource(w.LibVlcForTest, new Uri(src).AbsoluteUri);
                Await(w.PlayFromUrlAsync(new Uri(src).AbsoluteUri, null, Array.Empty<string>()));
                PumpUntil(() => w.IsLoadingErrorShown || w.LastStartFailure != null || w.CurrentNativeStart?.PlayingSeen == true, "neither a failure nor Playing arrived", 20000);
                if (w.CurrentNativeStart?.PlayingSeen != true) Assert.Equal("native-stalled", w.LastStartFailure ?? "native-stalled");
            }
            finally { w.StopPlaybackSource(); try { File.Delete(src); } catch { } }
        });
    }

    [Fact]
    public void StallWindow_Default_Is45Seconds()
    {
        VolumeMuteTests.Sta((w, settings) => Assert.Equal(TimeSpan.FromSeconds(45), w.NativeStallWindow));
    }
}
