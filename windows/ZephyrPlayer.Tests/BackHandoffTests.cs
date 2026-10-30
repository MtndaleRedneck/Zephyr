using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>CP2 round 5: the Back handoff, after run 41095411723 attempt 1 caught
    /// one all-black client frame at 438 ms of Back. The real Back path (library laid
    /// out under the player, its picture on the native video host, movie stopped and
    /// overlay hidden, player removed, host hidden, picture let go) runs with the
    /// shell's ordering trace on, and the order is asserted from the trace: the
    /// SCREEN, read back, must show the library picture where the host is and WPF's
    /// switched frame (the library caption colour) BEFORE the player leaves and the
    /// host hides, and the host is hidden before the picture is let go; nothing paints
    /// the host black on the way. The failing-path case paints the host black right
    /// after the teardown (a stale black remnant on the video host, the kind of frame
    /// the run caught): the handoff must see the black on screen, repaint the library
    /// and only then let the player go.</summary>
    public class BackHandoffTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

        private static void Sta(Action body, int timeoutMs = 120000)
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { error = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(timeoutMs), "STA body did not finish");
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        private static void Pump(int ms = 0)
        {
            var until = Environment.TickCount64 + ms;
            do
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (ms > 0) Thread.Sleep(15);
            } while (Environment.TickCount64 < until);
        }

        private static void PumpUntil(Func<bool> test, string message, int ms = 10000)
        {
            var until = Environment.TickCount64 + ms;
            while (!test() && Environment.TickCount64 < until) Pump(30);
            Assert.True(test(), message);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        private static int Index(List<SwitchTrace.Entry> ev, string name, int after = -1, Func<string, bool>? detail = null)
        {
            for (int i = after + 1; i < ev.Count; i++)
                if (ev[i].Event == name && (detail == null || detail(ev[i].Detail))) return i;
            return -1;
        }

        private static string Dump(List<SwitchTrace.Entry> ev, int from)
        {
            if (ev.Count == 0) return "(empty trace)";
            long t0 = ev[Math.Max(0, from)].Qpc;
            return string.Join("\n", ev.Skip(Math.Max(0, from)).Select(e => $"{(e.Qpc - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,8:0.0} ms  {e.Event} {e.Detail}"));
        }

        /// <summary>Plays the fixture clip in a real shell, then presses Back the way
        /// App.LeavePlayer does; [afterTeardown] runs right after the post-teardown
        /// repaint. Returns the trace from just before Back to the end of the handoff.</summary>
        private static (List<SwitchTrace.Entry> ev, string verdict, bool hostVisibleAfter) RunBack(string name, Action<IntPtr>? afterTeardown)
        {
            List<SwitchTrace.Entry> ev = new();
            string verdict = "";
            bool hostVisibleAfter = true;
            Core.Initialize();
            using var libvlc = new LibVLC("--no-video", "--aout=dummy");
            using var player = new MediaPlayer(libvlc);
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-r5-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
            var store = new PairedStore(Path.Combine(dir, "paired.json"));
            var shell = new ShellWindow(settings);
            var home = new HomeView(settings, store, _ => { }, () => { }, loadDurations: false);
            var view = new PlayerView(libvlc, player, settings);
            var clip = new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4")).AbsoluteUri;
            try
            {
                shell.ShowView(home);
                shell.Show();
                // On screen and on top, so the screen read-back sees the shell itself.
                shell.Left = 0; shell.Top = 0; shell.Width = 900; shell.Height = 620;
                shell.Topmost = true;
                shell.Activate();
                Pump(400);
                shell.ShowView(view);
                var start = view.PlayFromUrlAsync(clip, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted, "start never completed", 15000);
                start.GetAwaiter().GetResult();
                PumpUntil(() => player.State == VLCState.Playing && !view.IsLoadingShown, "clip never played", 15000);
                PumpUntil(() => shell.HeldUnderView == null && !shell.VideoHostHeld, "the library was never released after the player's own Playing");
                Pump(300);
                var host = shell.VideoHostHandle();
                Assert.NotEqual(IntPtr.Zero, host);
                Assert.True(IsWindowVisible(host), "the video host is not visible while the movie plays");

                SwitchTrace.DumpDir = OutDir;
                SwitchTrace.Begin();
                SwitchTrace.Mark("test.back", name);
                shell.TeardownPaintedForTest = afterTeardown;
                // App.LeavePlayer's Back.
                shell.ShowView(home, () => { view.StopPlaybackSource(); view.HideOverlayWindow(); });
                Assert.True(shell.LeavePending, "the player left in the same pass as Back, before any proof");
                Assert.True(shell.VideoHostHeld, "the host let go of the library picture before the player left");
                PumpUntil(() => !shell.LeavePending && !shell.VideoHostHeld, "the Back handoff never completed", 15000);
                Pump(200);
                hostVisibleAfter = IsWindowVisible(host);
                verdict = shell.LastHandoff;
                ev = SwitchTrace.End();
                try { File.WriteAllText(Path.Combine(OutDir, $"cp2-unit-r5-back-handoff-{name}.txt"), $"verdict: {verdict}\nhost visible after: {hostVisibleAfter}\n" + Dump(ev, 0)); } catch { }
            }
            finally
            {
                if (SwitchTrace.On) SwitchTrace.End();
                shell.TeardownPaintedForTest = null;
                view.ReleasePlayback();
                shell.Close();
                try { Directory.Delete(dir, true); } catch { }
            }
            return (ev, verdict, hostVisibleAfter);
        }

        /// <summary>The order every Back must keep.</summary>
        private static void AssertOrder(List<SwitchTrace.Entry> ev, string verdict, bool hostVisibleAfter)
        {
            var d = Dump(ev, 0);
            int back = Index(ev, "back.begin");
            Assert.True(back >= 0, "no back.begin in the trace:\n" + d);
            int hold = Index(ev, "hold.set", back);
            int seeThrough = Index(ev, "player.see-through", back);
            int paintBefore = Index(ev, "host.paint", hold, x => x.StartsWith("library-picture", StringComparison.Ordinal) && !x.Contains("FAILED"));
            int stopB = Index(ev, "stop.begin", back), stopE = Index(ev, "stop.end", back);
            int overlay = Index(ev, "overlay.hidden", stopE);
            int gone = Index(ev, "video-windows.after-stop", stopE, x => x.StartsWith("0 visible"));
            int repaint = Index(ev, "host.repaint", gone);
            int paintLib = Index(ev, "host.paint", repaint, x => x.StartsWith("library-picture", StringComparison.Ordinal) && !x.Contains("FAILED"));
            int handoff = Index(ev, "handoff.begin", back, x => x.Contains("host-check=True") && x.Contains("caption-check=True"));
            int hostProof = Index(ev, "presented.host", handoff, x => x.StartsWith("after-teardown: Match"));
            int wpfProof = Index(ev, "presented.wpf", handoff, x => x.StartsWith("Match"));
            int removed = Index(ev, "player.removed", back);
            int released = Index(ev, "hold.released", back);
            Assert.True(hold > back && hold < seeThrough, "the drawn library picture was not on the host first:\n" + d);
            Assert.True(paintBefore > hold && paintBefore < stopB, "the host was not painted with the library picture before the stop:\n" + d);
            Assert.True(seeThrough < stopB && stopB < stopE && stopE < overlay, "teardown order (see-through, stop, overlay):\n" + d);
            Assert.True(gone > overlay, "libVLC's video windows were not checked gone after the stop:\n" + d);
            Assert.True(repaint > gone && paintLib > repaint, "the host was not repainted with the library picture after the teardown:\n" + d);
            Assert.True(handoff > paintLib, "the handoff did not check both the host and the caption:\n" + d);
            Assert.True(hostProof > handoff, "the screen never showed the library picture on the host after the teardown:\n" + d);
            Assert.True(wpfProof > handoff, "the screen never showed WPF's switched frame (library caption colour):\n" + d);
            Assert.True(removed > hostProof && removed > wpfProof, "the player left before the screen showed the library:\n" + d);
            Assert.True(released > removed, "the library picture was let go before the player left:\n" + d);
            Assert.Equal("host-hidden", ev[released].Detail);
            Assert.False(hostVisibleAfter, "the video host is still visible after Back");
            Assert.Equal("proven", verdict);
            // Nothing paints the host black between Back and the release of the picture.
            for (int i = back; i <= released; i++)
                Assert.False(ev[i].Event == "host.paint" && ev[i].Detail == "black", $"the host painted black at trace line {i}:\n" + d);
            Assert.Equal(-1, Index(ev, "hold.set-NOT-DRAWN", back));
        }

        [Fact]
        public void Back_ScreenShowsTheLibrary_BeforeThePlayerLeaves_HostHidesBeforeThePictureIsLetGo()
        {
            Sta(() =>
            {
                var (ev, verdict, hostVisibleAfter) = RunBack("normal", null);
                AssertOrder(ev, verdict, hostVisibleAfter);
            });
        }

        [Fact]
        public void Back_BlackOnTheHostAfterTeardown_IsSeenOnScreen_AndRepaintedBeforeThePlayerLeaves()
        {
            Sta(() =>
            {
                // A stale black frame on the video host right after the movie stopped:
                // painted straight onto the host's DC, outside the shell's own paint.
                // Painted again every 2 ms from another thread (a natural WM_PAINT, e.g.
                // when the overlay window goes, would otherwise wipe one black paint
                // before the screen is read: run 41114710721) until the handoff has
                // reported the mismatch, at most 1.5 s.
                var (ev, verdict, hostVisibleAfter) = RunBack("black-after-teardown", host =>
                {
                    SwitchTrace.Mark("test.paint-host-black");
                    void Paint()
                    {
                        var dc = GetDC(host);
                        try { GetClientRect(host, out var r); FillRect(dc, ref r, ShellWindow.BlackBrush); }
                        finally { ReleaseDC(host, dc); }
                        GdiFlush();
                    }
                    Paint();
                    new Thread(() =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 1500)
                        {
                            var snap = SwitchTrace.Snapshot();
                            int b = snap.FindIndex(x => x.Event == "test.paint-host-black");
                            if (b >= 0 && snap.FindIndex(b, x => x.Event == "presented.host-mismatch") >= 0) { SwitchTrace.Mark("test.black-painter-stopped"); return; }
                            Paint();
                            Thread.Sleep(2);
                        }
                        SwitchTrace.Mark("test.black-painter-timed-out");
                    }) { IsBackground = true }.Start();
                });
                var d = Dump(ev, 0);
                AssertOrder(ev, verdict, hostVisibleAfter);
                int black = Index(ev, "test.paint-host-black");
                int mismatch = Index(ev, "presented.host-mismatch", black, x => x.StartsWith("after-teardown"));
                int repaint = Index(ev, "host.repaint", mismatch);
                int proof = Index(ev, "presented.host", repaint, x => x.StartsWith("after-teardown: Match"));
                int removed = Index(ev, "player.removed", black);
                Assert.True(black >= 0 && mismatch > black, "the black host was not seen on screen by the handoff:\n" + d);
                Assert.True(repaint > mismatch && proof > repaint, "the host was not repainted and proven after the black was seen:\n" + d);
                Assert.True(removed > proof, "the player left while the host could still show black:\n" + d);
            });
        }
    }
}
