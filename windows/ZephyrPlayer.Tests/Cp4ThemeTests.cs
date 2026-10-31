using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>CP4 items 84-86: the dark dialog in the 2.0 theme with the shared
    /// hover/pressed layers, and the settings-write failure on that dialog instead of the
    /// stock light MessageBox.</summary>
    public class Cp4ThemeTests
    {
        private static void Sta(Action body)
        {
            Exception? error = null;
            var t = new Thread(() => { try { body(); } catch (Exception e) { error = e; } finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
            t.SetApartmentState(ApartmentState.STA); t.Start();
            Assert.True(t.Join(60000), "STA body timed out");
            if (error != null) throw new Exception("STA body failed", error);
        }

        private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T hit && match(hit)) return hit;
                var deeper = Find(c, match);
                if (deeper != null) return deeper;
            }
            return null;
        }

        internal static void Save(Visual v, int w, int h, string name)
        {
            var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rtb));
            var dir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
            using var fs = File.Create(Path.Combine(dir, name));
            png.Save(fs);
        }

        [Fact]
        public void DarkDialog_Themed_WithHoverPressedLayers()
        {
            Sta(() =>
            {
                var dlg = FirewallSetup.NoteWindow("Theme check.", "Zephyr Player");
                dlg.ShowInTaskbar = false;
                dlg.Show(); dlg.UpdateLayout();
                Assert.Equal(Color.FromRgb(0x0E, 0x0E, 0x11), ((SolidColorBrush)dlg.Background).Color);
                var ok = Find<Button>(dlg, _ => true)!;
                Assert.NotNull(ok);
                ok.ApplyTemplate();
                var hl = (Border)ok.Template.FindName("Hl", ok);
                var pr = (Border)ok.Template.FindName("Pr", ok);
                Assert.NotNull(hl); Assert.NotNull(pr);
                Assert.Equal(0.0, hl.Opacity); Assert.Equal(0.0, pr.Opacity);
                Assert.Equal(Colors.White, ((SolidColorBrush)hl.Background).Color);
                Assert.IsType<LinearGradientBrush>(ok.Background);   // .btn.or accent
                Save(dlg, (int)dlg.ActualWidth, (int)dlg.ActualHeight, "cp4-dark-dialog-render.png");
                dlg.Close();
            });
        }

        [Fact]
        public void Theme_ButtonStyles_CarryHoverAndPressedLayers()
        {
            Sta(() =>
            {
                var themeHost = new PlayerBar();   // merges Theme.xaml
                foreach (var key in new[] { "ZBarButton", "ZGlyphButton", "ZPopupRow", "ZChip", "ZBoxButton", "ZTextButton", "ZLinkButton", "ZNavItem" })
                {
                    var b = new Button { Style = (Style)themeHost.FindResource(key), Content = "x", Width = 60, Height = 30 };
                    var host = new Window { Content = b, Width = 120, Height = 90, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                    host.Show(); b.ApplyTemplate();
                    Assert.True(b.Template.FindName("Hl", b) is Border, key + " has no hover layer");
                    Assert.True(b.Template.FindName("Pr", b) is Border, key + " has no pressed layer");
                    host.Close();
                }
            });
        }

        [Fact]
        public void SettingsWriteFailure_UsesThemedDialog()
        {
            Sta(() =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp4-write-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "settings.json");
                File.WriteAllText(path, "{ not json");
                Core.Initialize();
                using var libvlc = new LibVLC();
                using var player = new LibVLCSharp.Shared.MediaPlayer(libvlc);
                var settings = new PlayerSettings(path);
                var view = new PlayerView(libvlc, player, settings, new VolumeService());
                Window? shown = null;
                Action<Window> h = d =>
                {
                    shown = d;
                    d.ShowInTaskbar = false;
                    d.ContentRendered += (_, __) =>
                    {
                        try { Save(d, (int)d.ActualWidth, (int)d.ActualHeight, "cp4-settings-write-failure.png"); } catch { }
                        d.Close();
                    };
                };
                PlayerView.SettingsWarningShown += h;
                try
                {
                    settings.PrimarySkipSeconds = 15;   // write refused: the file is unreadable
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var until = DateTime.UtcNow.AddSeconds(15);
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    timer.Tick += (_, __) => { if (shown != null && !shown.IsVisible || DateTime.UtcNow > until) { timer.Stop(); frame.Continue = false; } };
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                finally { PlayerView.SettingsWarningShown -= h; }
                Assert.NotNull(shown);
                Assert.Equal(Color.FromRgb(0x0E, 0x0E, 0x11), ((SolidColorBrush)shown!.Background).Color);
                Assert.Equal("Settings could not be saved", shown.Title);
                Assert.Equal("{ not json", File.ReadAllText(path));   // original preserved
                view.ReleasePlayback();
            });
        }
    }
}
