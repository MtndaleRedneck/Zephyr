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
            Assert.Null(error);
            try { File.Delete(path); } catch { }
        }

        private static Color StrokeOf(System.Windows.Shapes.Shape s) => ((SolidColorBrush)s.Stroke).Color;

        [Fact]
        public void Defaults_Match_The_Approved_Direction()
        {
            Sta((settings, w) =>
            {
                Assert.Equal("5", w.FindName("SkipBackLabel") is System.Windows.Controls.TextBlock b ? b.Text : "");
                Assert.Equal("30", w.FindName("SkipForwardLabel") is System.Windows.Controls.TextBlock f ? f.Text : "");
                // Speed + sleep hidden by default (his "Both hidden by default in settings").
                Assert.Equal(Visibility.Collapsed, ((UIElement)w.FindName("SpeedButton")!).Visibility);
                Assert.Equal(Visibility.Collapsed, ((UIElement)w.FindName("SleepButton")!).Visibility);
                // Volume left, icons right (his final correction): volume stack is column 0.
                var volumeStack = (StackPanel)((Button)w.FindName("VolumeButton")!).Parent;
                Assert.Equal(0, Grid.GetColumn(volumeStack));
            });
        }

        [Fact]
        public void Settings_Change_Restyles_Live()
        {
            Sta((settings, w) =>
            {
                settings.SkipBackSeconds = 10;
                settings.SkipForwardSeconds = 60;
                settings.BarColorStyle = "orange";
                settings.IconStyle = "filled";
                settings.ShowSpeed = true;
                settings.ShowSleepTimer = true;
                w.Dispatcher.Invoke(() => { }); // flush the async Changed handlers
                Assert.Equal("10", ((System.Windows.Controls.TextBlock)w.FindName("SkipBackLabel")!).Text);
                Assert.Equal("60", ((System.Windows.Controls.TextBlock)w.FindName("SkipForwardLabel")!).Text);
                Assert.Equal(Color.FromRgb(0xF9, 0x7C, 0x02), StrokeOf((System.Windows.Shapes.Shape)w.FindName("SkipBackRing")!));
                Assert.Equal(Color.FromRgb(0xF9, 0x7C, 0x02), StrokeOf((System.Windows.Shapes.Shape)w.FindName("PlayRing")!));
                Assert.Equal(Visibility.Visible, ((UIElement)w.FindName("SpeedButton")!).Visibility);
                Assert.Equal(Visibility.Visible, ((UIElement)w.FindName("SleepButton")!).Visibility);
            });
        }
    }
}
