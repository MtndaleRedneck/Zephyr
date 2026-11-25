using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Carried from 2.0.2 Cp1Shell: bar controls that never take
    /// keyboard focus, one key map (Up/Down volume), local sidecar subtitles with
    /// libVLC's "Disable" (-1) entry filtered, remembered shell geometry, and the
    /// render-01 library composition rendered at 1600x1000.
    /// </summary>
    public class KeyMapCarriedTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

        private static void Sta(Action body, int timeoutMs = 60000)
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

        private static void PumpUntil(Func<bool> test, string message, int ms = 8000)
        {
            var until = Environment.TickCount64 + ms;
            while (!test() && Environment.TickCount64 < until) Pump(30);
            Assert.True(test(), message);
        }

        private static string TempSettings() => Path.Combine(Path.GetTempPath(), "zephyr-cp1-" + Guid.NewGuid().ToString("N") + ".json");

        // ---------- keyboard (P0-4) ----------

        [Fact]
        public void BarControls_NeverTakeKeyboardFocus()
        {
            Sta(() =>
            {
                Core.Initialize();
                using var libvlc = new LibVLC();
                using var player = new MediaPlayer(libvlc);
                var view = new MainWindow(libvlc, player, new PlayerSettings(TempSettings()), null);
                var overlay = (FrameworkElement)view.FindName("Overlay")!;
                var found = 0;
                var focusable = new System.Collections.Generic.List<string>();
                void Walk(object node)
                {
                    if (node is ButtonBase or Slider)
                    {
                        found++;
                        var fe = (FrameworkElement)node;
                        if (fe.Focusable) focusable.Add(fe.Name.Length > 0 ? fe.Name : fe.GetType().Name);
                    }
                    if (node is DependencyObject d)
                        foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
                }
                Walk(overlay);
                Assert.True(found >= 12, "expected every bar button and slider, found " + found);
                // Scoped 3.0 contract: the fullscreen More button is the ONE bar control that takes keyboard focus (Tab-reachable overflow menu,
                // SkipPairUiTests.More_Menu...). Every other bar button and slider stays non-focusable: a click on one would strand the key map.
                Assert.True(focusable.SequenceEqual(new[] { "MoreButton" }), "focusable bar controls must be exactly [MoreButton], found [" + string.Join(", ", focusable) + "]");
                view.ReleasePlayback();
            });
        }

        [Fact]
        public void MoreButton_HasFocus_OldKeyMapStillRoutesFromIt_AndEnterIsLeftToTheButton()
        {
            Sta(() =>
            {
                Core.Initialize();
                using var libvlc = new LibVLC();
                using var player = new MediaPlayer(libvlc);
                var view = new MainWindow(libvlc, player, new PlayerSettings(TempSettings()), null);
                var host = view; host.Width = 800; host.Height = 450; host.Left = -20000; host.Top = -20000; host.ShowInTaskbar = false; host.WindowStartupLocation = WindowStartupLocation.Manual;
                host.Show();
                try
                {
                    var more = (Button)view.FindName("MoreButton")!;
                    more.Visibility = Visibility.Visible; // the state SetFullscreen(true) gives it, without maximizing a test window
                    view.UpdateLayout();
                    Assert.True(more.Focusable && more.IsTabStop && more.IsEnabled, "More must be Tab-reachable");
                    Assert.True(more.Focus(), "More could not take keyboard focus");
                    Assert.Same(more, FocusManager.GetFocusedElement(view));
                    var source = PresentationSource.FromVisual(host)!;
                    // Keys raised FROM the focused More button tunnel through the window's PreviewKeyDown, the same one key map.
                    KeyEventArgs From(Key k) { var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = more }; more.RaiseEvent(e); return e; }
                    view.SetVolumeTied(50);
                    var up = From(System.Windows.Input.Key.Up);
                    Assert.True(up.Handled);
                    Assert.Equal(55, view.SharedVolume);
                    var m = From(System.Windows.Input.Key.M);
                    Assert.True(m.Handled);
                    Assert.True(view.SharedMute);
                    From(System.Windows.Input.Key.M);
                    Assert.False(view.SharedMute);
                    Assert.True(From(System.Windows.Input.Key.Space).Handled); // Space stays the player's play/pause, never More's click
                    Assert.False(From(System.Windows.Input.Key.Enter).Handled); // Enter is not in the key map: left to the focused button
                    Assert.False(From(System.Windows.Input.Key.Escape).Handled); // Esc means something only in full screen or with a pop-up open
                }
                finally { view.ReleasePlayback(); }
            });
        }

        [Fact]
        public void KeyMap_UpDownVolume_Mute_AndUnknownKeysPassThrough()
        {
            Sta(() =>
            {
                Core.Initialize();
                using var libvlc = new LibVLC();
                using var player = new MediaPlayer(libvlc);
                var view = new MainWindow(libvlc, player, new PlayerSettings(TempSettings()), null);
                var host = view; host.Width = 800; host.Height = 450; host.Left = -20000; host.Top = -20000; host.ShowInTaskbar = false; host.WindowStartupLocation = WindowStartupLocation.Manual;
                host.Show();
                try
                {
                    var source = PresentationSource.FromVisual(host)!;
                    KeyEventArgs Key(Key k) => new(Keyboard.PrimaryDevice, source, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    view.SetVolumeTied(50);
                    var up = Key(System.Windows.Input.Key.Up); view.HandleKey(up);
                    Assert.True(up.Handled);
                    Assert.Equal(55, view.SharedVolume);
                    var down = Key(System.Windows.Input.Key.Down); view.HandleKey(down);
                    view.HandleKey(Key(System.Windows.Input.Key.Down));
                    Assert.Equal(45, view.SharedVolume);
                    Assert.Equal("45%", ((TextBlock)view.FindName("VolumeLabel")!).Text);
                    var m = Key(System.Windows.Input.Key.M); view.HandleKey(m);
                    Assert.True(view.SharedMute);
                    view.HandleKey(Key(System.Windows.Input.Key.M));
                    Assert.False(view.SharedMute);
                    var space = Key(System.Windows.Input.Key.Space); view.HandleKey(space);
                    Assert.True(space.Handled); // Space is the player's, never a focused button's "click"
                    var other = Key(System.Windows.Input.Key.A); view.HandleKey(other);
                    Assert.False(other.Handled);
                    var esc = Key(System.Windows.Input.Key.Escape); view.HandleKey(esc);
                    Assert.False(esc.Handled); // Esc only means something in full screen
                }
                finally { view.ReleasePlayback(); }
            });
        }
    }
}
