using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using LibVLCSharp.Shared;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>Scrub hygiene: losing mouse capture ends the scrub (no stuck timeline/autohide),
    /// and live seeks are committed on an intentional throttle while the visual scrub stays continuous.</summary>
    public class SeekScrubUiTests
    {
        [Fact]
        public void CommitDue_ThrottlesAt75Ms_AndAlwaysFiresFirstTime()
        {
            var t0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(PlayerBarLogic.SeekCommitDue(DateTime.MinValue, t0));
            Assert.False(PlayerBarLogic.SeekCommitDue(t0, t0.AddMilliseconds(74)));
            Assert.True(PlayerBarLogic.SeekCommitDue(t0, t0.AddMilliseconds(75)));
            Assert.True(PlayerBarLogic.SeekCommitDue(t0, t0.AddMilliseconds(200)));
        }

        [Fact]
        public void ReleaseCommits_OnlyAfterAnActiveScrubOnLoadedMedia()
        {
            // The discriminating case a no-media harness cannot reach: a lone release over loaded
            // media must not commit the stale thumb position.
            Assert.False(PlayerBarLogic.SeekReleaseCommits(seeking: false, opening: false, mediaLength: 60000));
            Assert.False(PlayerBarLogic.SeekReleaseCommits(seeking: false, opening: false, mediaLength: 0));
            Assert.False(PlayerBarLogic.SeekReleaseCommits(seeking: true, opening: true, mediaLength: 60000));
            Assert.False(PlayerBarLogic.SeekReleaseCommits(seeking: true, opening: false, mediaLength: 0));
            Assert.True(PlayerBarLogic.SeekReleaseCommits(seeking: true, opening: false, mediaLength: 60000));
        }

        [Fact]
        public void LoneMouseUp_WithoutAPress_CommitsNothing()
        {
            Exception? error = null;
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-seekup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var thread = new Thread(() =>
            {
                try
                {
                    NativeVlc.Init();
                    using var libvlc = new LibVLC();
                    using var player = new MediaPlayer(libvlc);
                    var w = new MainWindow(libvlc, player, new PlayerSettings(Path.Combine(dir, "settings.json")), new VolumeService());
                    w.WindowStyle = WindowStyle.None;
                    w.Left = -20000; w.Top = -20000;
                    w.Show();
                    w.UpdateLayout();
                    // A release that never saw a press (press started on another control, capture
                    // already lost): the guard must no-op instead of touching the bar or player.
                    w.SeekBar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseUpEvent, Source = w.SeekBar });
                    Assert.False(w.SeekingNow, "lone mouse-up must not start or leave a scrub");
                    Assert.False(w.SeekBar.IsMouseCaptured, "lone mouse-up must not capture the mouse");
                    Assert.Equal(0, w.SeekCommitCount);
                    w.Close();
                }
                catch (Exception ex) { error = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(90000), "lone mouse-up STA test did not finish");
            try { Directory.Delete(dir, true); } catch { }
            if (error != null) throw new Exception("lone mouse-up test failed", error);
        }

        [Fact]
        public void LostCapture_EndsScrub()
        {
            Exception? error = null;
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-seek-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var thread = new Thread(() =>
            {
                try
                {
                    NativeVlc.Init();
                    using var libvlc = new LibVLC();
                    using var player = new MediaPlayer(libvlc);
                    var w = new MainWindow(libvlc, player, new PlayerSettings(Path.Combine(dir, "settings.json")), new VolumeService());
                    w.WindowStyle = WindowStyle.None;
                    w.Left = -20000; w.Top = -20000;
                    w.Show();
                    w.UpdateLayout();
                    // XAML wires the generic PreviewMouseDown; raising the Left-specific event would
                    // not reach that handler.
                    w.SeekBar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = w.SeekBar });
                    Assert.True(w.SeekingNow, "press starts the scrub");
                    w.SeekBar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        { RoutedEvent = UIElement.LostMouseCaptureEvent, Source = w.SeekBar });
                    Assert.False(w.SeekingNow, "losing capture ends the scrub");

                    // Full gesture ordering: mouse-up ends the scrub BEFORE capture release, so the
                    // resulting LostMouseCapture is a no-op (single commit, no double seek).
                    w.SeekBar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = w.SeekBar });
                    Assert.True(w.SeekingNow, "second press starts the scrub again");
                    w.SeekBar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseUpEvent, Source = w.SeekBar });
                    Assert.False(w.SeekingNow, "mouse-up ends the scrub");
                    w.SeekBar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        { RoutedEvent = UIElement.LostMouseCaptureEvent, Source = w.SeekBar });
                    Assert.False(w.SeekingNow, "capture loss after mouse-up changes nothing");
                    Assert.Equal(0, w.SeekCommitCount); // no media loaded in this test: nothing may commit
                    w.Close();
                }
                catch (Exception ex) { error = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(90000), "Seek STA test did not finish");
            try { Directory.Delete(dir, true); } catch { }
            if (error != null) throw new Exception("Seek scrub UI test failed", error);
        }
    }
}
