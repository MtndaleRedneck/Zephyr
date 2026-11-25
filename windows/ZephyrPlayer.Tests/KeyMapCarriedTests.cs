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
                void Walk(object node)
                {
                    if (node is ButtonBase or Slider)
                    {
                        found++;
                        var fe = (FrameworkElement)node;
                        Assert.False(fe.Focusable, (fe.Name.Length > 0 ? fe.Name : fe.GetType().Name) + " can take keyboard focus: a click would strand the key map");
                    }
                    if (node is DependencyObject d)
                        foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
                }
                Walk(overlay);
                Assert.True(found >= 12, "expected every bar button and slider, found " + found);
                view.ReleasePlayback();
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
