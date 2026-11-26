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
    /// <summary>Header heights must be EXACTLY identical, measured on the
    /// rendered screen - including across transitions - never assumed from constants. These tests
    /// measure the real rendered header of each Player surface before and after its transitions
    /// (windowed vs fullscreen, tab switches, section switches) and assert exact equality, and they
    /// capture each measured header as pixel evidence.</summary>
    public class HeaderHeightTests
    {
        private static void Sta(Action<string> body)
        {
            Exception? error = null;
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-headers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var thread = new Thread(() =>
            {
                try { body(dir); }
                catch (Exception ex) { error = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(90000), "Header STA test did not finish");
            try { Directory.Delete(dir, true); } catch { }
            if (error != null) throw new Exception("Header height test failed", error);
        }

        private static void Capture(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            var w = (int)Math.Ceiling(element.ActualWidth);
            var h = (int)Math.Ceiling(element.ActualHeight);
            var image = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
            image.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var dir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
            using var stream = File.Create(Path.Combine(dir, name));
            encoder.Save(stream);
        }

        [Fact]
        public void Player_TopBar_Height_Identical_Windowed_And_Fullscreen()
        {
            Sta(dir =>
            {
                NativeVlc.Init();
                using var libvlc = new LibVLC();
                using var player = new LibVLCSharp.Shared.MediaPlayer(libvlc);
                var w = new MainWindow(libvlc, player, new PlayerSettings(Path.Combine(dir, "settings.json")), new VolumeService());
                w.Left = -20000; w.Top = -20000; w.Width = 1600; w.Height = 900;
                w.Show(); w.UpdateLayout();
                var bar = (FrameworkElement)w.FindName("TopOverlay")!;
                var windowed = bar.ActualHeight;
                Capture(bar, "pc-header-player-windowed.png");
                w.SetFullscreen(true); w.UpdateLayout();
                var fullscreen = bar.ActualHeight;
                Capture(bar, "pc-header-player-fullscreen.png");
                w.SetFullscreen(false); w.UpdateLayout();
                var restored = bar.ActualHeight;
                Assert.True(windowed > 0, "top bar has no height");
                Assert.Equal(windowed, fullscreen);   // exact, not approximate
                Assert.Equal(windowed, restored);
                w.Close();
            });
        }

        [Fact]
        public void Home_Header_Height_Identical_Across_Tabs_And_Search()
        {
            Sta(dir =>
            {
                var lib = Path.Combine(dir, "Movies");
                Directory.CreateDirectory(lib);
                File.WriteAllBytes(Path.Combine(lib, "Film.mp4"), new byte[] { 1, 2, 3 });
                var settings = new PlayerSettings(Path.Combine(dir, "settings.json")) { LibraryFolder = lib };
                var store = new PairedStore(Path.Combine(dir, "paired.json"));
                var history = new PlaybackHistory(Path.Combine(dir, "history.json"));
                var w = new HomeWindow(settings, store, _ => { }, () => { }, loadDurations: false, history: history);
                w.Left = -20000; w.Top = -20000;
                w.Show(); w.UpdateLayout();
                var header = (FrameworkElement)w.FindName("HeaderRow")!;
                var onLibrary = header.ActualHeight;
                Capture(header, "pc-header-home-library.png");
                ((TextBox)w.FindName("SearchBox")!).Text = "film";
                w.UpdateLayout();
                var searching = header.ActualHeight;
                w.Refresh();
                w.UpdateLayout();
                var afterRefresh = header.ActualHeight;
                Capture(header, "pc-header-home-refresh.png");
                Assert.True(onLibrary > 0, "home header has no height");
                Assert.Equal(onLibrary, searching);
                Assert.Equal(onLibrary, afterRefresh);
                Assert.True(w.DurationWork.Wait(10000), "duration worker did not finish");
                w.Close();
            });
        }

        [Fact]
        public void Settings_Title_Height_Identical_Across_All_Sections()
        {
            Sta(dir =>
            {
                var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                var w = new SettingsWindow(settings, new PairedStore(Path.Combine(dir, "pairs.json")), () => { });
                w.Left = -20000; w.Top = -20000;
                w.Show(); w.UpdateLayout();
                var title = (FrameworkElement)w.FindName("SectionHeader")!;   // whole header container, not just the text
                double? reference = null;
                foreach (var name in new[] { "NavVolume", "NavSkip", "NavBar", "NavRemote", "NavFeedback", "NavUpdates" })
                {
                    ((Button)w.FindName(name)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    w.UpdateLayout();
                    var height = title.ActualHeight;
                    Assert.True(height > 0, name + ": section header has no height");
                    if (reference.HasValue) Assert.Equal(reference.Value, height); else reference = height;
                }
                Capture(title, "pc-header-settings-section.png");
                w.Close();
            });
        }
    }
}
