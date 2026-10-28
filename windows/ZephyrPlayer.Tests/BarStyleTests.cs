using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Player-bar behavior: default placements,
    /// skip lengths from Settings, color/icon styles, speed+sleep hidden by default.
    /// WPF needs STA; no display required for property assertions.
    /// </summary>
    public class BarStyleTests
    {
        private static void Sta(Action<PlayerSettings, MainWindow> body)
        {
            string? error = null;
            var path = Path.Combine(Path.GetTempPath(), "zephyr-barstyle-" + Guid.NewGuid().ToString("N") + ".json");
            var t = new Thread(() =>
            {
                try
                {
                    Core.Initialize();
                    var libvlc = new LibVLC();
                    var player = new MediaPlayer(libvlc);
                    var settings = new PlayerSettings(path);
                    var w = new MainWindow(libvlc, player, settings, new VolumeService());
                    body(settings, w);
                    w.Close();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch (Exception e) { error = e.ToString(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(30000);
                        if (error != null)
            {
                var f = System.IO.Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? System.IO.Path.GetTempPath(), "sta-error-" + Guid.NewGuid().ToString("N") + ".txt");
                try { System.IO.File.WriteAllText(f, error); } catch { }
                Assert.True(false, "STA body error (full text also in " + f + "):\n" + error);
            }
            try { File.Delete(path); } catch { }
        }

        private static Color StrokeOf(System.Windows.Shapes.Shape s) => ((SolidColorBrush)s.Stroke).Color;

        [Fact]
        public void Defaults_Match_The_Approved_Direction()
        {
            Sta((settings, w) =>
            {
                Assert.Equal("10", w.FindName("SkipBackLabel") is System.Windows.Controls.TextBlock b ? b.Text : "");
                Assert.Equal("10", w.FindName("SkipForwardLabel") is System.Windows.Controls.TextBlock f ? f.Text : "");
                Assert.Equal("30", ((TextBlock)w.FindName("SecondarySkipBackLabel")!).Text);
                Assert.Equal("30", ((TextBlock)w.FindName("SecondarySkipForwardLabel")!).Text);
                // Speed + sleep hidden by default.
                Assert.Equal(Visibility.Collapsed, ((UIElement)w.FindName("SpeedButton")!).Visibility);
                Assert.Equal(Visibility.Collapsed, ((UIElement)w.FindName("SleepButton")!).Visibility);
                // Volume left, icons right: volume stack is column 0.
                var volumeStack = (StackPanel)((Button)w.FindName("VolumeButton")!).Parent;
                Assert.Equal(0, Grid.GetColumn(volumeStack));
                // Unset default: the bar is orange when no style was ever picked.
                Assert.Equal(Color.FromRgb(0xFF, 0x95, 0x00), StrokeOf((System.Windows.Shapes.Shape)w.FindName("SkipBackRing")!));
            });
        }

        [Fact]
        public void Settings_Change_Restyles_Live()
        {
            Sta((settings, w) =>
            {
                settings.PrimarySkipSeconds = 10;
                settings.SecondarySkipSeconds = 60;
                settings.BarColorStyle = "orange";
                settings.IconStyle = "filled";
                settings.ShowSpeed = true;
                settings.ShowSleepTimer = true;
                // Flush the queued Changed handlers: a bare Invoke runs at Send priority and
                // jumps AHEAD of the Normal-priority ApplyBarStyle callbacks; pumping at
                // SystemIdle waits until they have all run.
                w.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.SystemIdle);
                Assert.Equal("10", ((System.Windows.Controls.TextBlock)w.FindName("SkipBackLabel")!).Text);
                Assert.Equal("10", ((System.Windows.Controls.TextBlock)w.FindName("SkipForwardLabel")!).Text);
                Assert.Equal(Color.FromRgb(0xFF, 0x95, 0x00), StrokeOf((System.Windows.Shapes.Shape)w.FindName("SkipBackRing")!));
                // Settled units: 60 reads "1m", never raw seconds.
                Assert.Equal("1m", ((TextBlock)w.FindName("SecondarySkipBackLabel")!).Text);
                Assert.Equal("1m", ((TextBlock)w.FindName("SecondarySkipForwardLabel")!).Text);
                // Secondary icons are double-triangles (fill restyles, no ring).
                Assert.Equal(Color.FromRgb(0xFF, 0x95, 0x00), ((SolidColorBrush)((System.Windows.Shapes.Shape)w.FindName("SecondarySkipForwardIcon")!).Stroke).Color);
                // Filled play/pause = colored DISC: the design fills the ring and
                // clears its stroke (hollow keeps the stroke instead). Assert the
                // filled semantics, not the hollow one.
                Assert.Equal(Color.FromRgb(0xFF, 0x95, 0x00), ((SolidColorBrush)((System.Windows.Shapes.Shape)w.FindName("PlayRing")!).Fill).Color);
                Assert.Equal(Colors.Transparent, StrokeOf((System.Windows.Shapes.Shape)w.FindName("PlayRing")!));
                Assert.Equal(Visibility.Visible, ((UIElement)w.FindName("SpeedButton")!).Visibility);
                Assert.Equal(Visibility.Visible, ((UIElement)w.FindName("SleepButton")!).Visibility);
            });
        }
    }
}
