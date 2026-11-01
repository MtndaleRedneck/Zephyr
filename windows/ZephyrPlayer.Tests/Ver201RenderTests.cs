using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>2.0.1 (Ezzy's Nov 1 list): Home with a REAL MKV library shows real frames
    /// in its tiles and the whole path; the bar and Home render at 100/150/200 % for the
    /// evidence set (RUNNER_TEMP/ver201-*.png); the Volume card is a real switch.</summary>
    public class Ver201RenderTests
    {
        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        private static void Pump(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(20);
            }
        }

        private static void Save(FrameworkElement root, int w, int h, int dpi, string name)
        {
            root.Measure(new Size(w, h)); root.Arrange(new Rect(0, 0, w, h)); root.UpdateLayout();
            var rtb = new RenderTargetBitmap(w * dpi / 96, h * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rtb));
            var outDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
            using var fs = File.Create(Path.Combine(outDir, $"ver201-{name}-{dpi}.png"));
            png.Save(fs);
        }

        [Fact]
        public void Home_RealMkvLibrary_TilesGetRealFrames_PathIsWhole_RenderedAt3Scales()
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "zephyr-201-" + Guid.NewGuid().ToString("N"));
                    var lib = Path.Combine(dir, "Users", "Ezzy", "Videos", "Movies");
                    Directory.CreateDirectory(Path.Combine(lib, "Dunkirk 2017"));
                    File.Copy(Fixture("clip.mkv"), Path.Combine(lib, "Coyote vs Acme.mkv"));
                    File.Copy(Fixture("thumb-hevc.mkv"), Path.Combine(lib, "The Sheep Detectives 2026.mkv"));
                    File.Copy(Fixture("thumb-hevc.mkv"), Path.Combine(lib, "Dunkirk 2017", "Dunkirk.mkv"));
                    var prevCache = VlcThumbs.CacheDir;
                    VlcThumbs.CacheDir = Path.Combine(dir, "thumbs");
                    try
                    {
                        var settings = new PlayerSettings(Path.Combine(dir, "settings.json")) { LibraryFolder = lib };
                        var store = new PairedStore(Path.Combine(dir, "paired.json"));
                        var w = new HomeView(settings, store, _ => { }, () => { }, loadDurations: true);
                        var host = new Window { Content = w, Width = 1280, Height = 760, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                        host.Show();
                        var list = (ListBox)w.FindName("MovieList")!;
                        Assert.Equal(3, list.Items.Count);
                        object? Prop(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o);
                        var deadline = DateTime.UtcNow.AddSeconds(45);
                        while (DateTime.UtcNow < deadline && list.Items.Cast<object>().Any(e => (bool)Prop(e, "Dir")! ? Prop(e, "Peek") == null : Prop(e, "Thumb") == null)) Pump(250);
                        Assert.True(w.DurationWork.Wait(20000), "duration worker did not finish");
                        Pump(300);
                        foreach (var e in list.Items.Cast<object>())
                        {
                            var isDir = (bool)Prop(e, "Dir")!;
                            Assert.True((isDir ? Prop(e, "Peek") : Prop(e, "Thumb")) != null, $"no picture for {Prop(e, "Name")}");
                            // Time badge on every movie (2.0.0 read no length through its stream input).
                            if (!isDir) Assert.True(Equals(Prop(e, "DurationVis"), Visibility.Visible), $"no time badge for {Prop(e, "Name")}");
                        }
                        // The whole path above the library root, not "C: > ... > Movies".
                        var crumbs = (ItemsControl)w.FindName("Breadcrumb")!;
                        var texts = crumbs.Items.OfType<TextBlock>().Select(x => x.Text).ToList();
                        Assert.Contains("Videos", texts);
                        Assert.Contains("Ezzy", texts);
                        Assert.Equal(lib, crumbs.ToolTip as string);
                        // One phone pill, no duplicate "Pair phones".
                        Assert.Null(w.FindName("PairButton"));
                        Pump(300);
                        foreach (var dpi in new[] { 96, 144, 192 }) Save((FrameworkElement)w.Content, 1280, 760, dpi, "home");
                        Assert.True(w.DurationWork.Wait(15000), "duration worker did not finish");
                        host.Close();
                    }
                    finally { VlcThumbs.CacheDir = prevCache; }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch (Exception e) { error = e.ToString(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(90000), "STA thread did not finish");
            Assert.True(error == null, error);
        }

        [Fact]
        public void PlayerBar_RendersAt3Scales_NoEffectOnTheBarItself()
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "zephyr-201b-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                    var bar = new PlayerBar();
                    bar.ShowAsPreview(settings);
                    // The shadow lives on its own layer; the bar (all text and glyphs) has no Effect.
                    Assert.Null(bar.ControlBar.Effect);
                    var host = new Grid { Width = 1100, Height = 150, Background = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x40)), Children = { bar } };
                    foreach (var dpi in new[] { 96, 144, 192 }) Save(host, 1100, 150, dpi, "bar");
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch (Exception e) { error = e.ToString(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(60000), "STA thread did not finish");
            Assert.True(error == null, error);
        }

        [Fact]
        public void RememberVolume_IsASwitch_OffStartsAt100()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-201c-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var s = new PlayerSettings(Path.Combine(dir, "settings.json"));
            Assert.True(s.RememberVolume);
            s.LastVolume = 37;
            Assert.Equal(37, s.StartVolume);
            s.RememberVolume = false;
            Assert.Equal(100, s.StartVolume);
            Assert.Equal(37, new PlayerSettings(Path.Combine(dir, "settings.json")).LastVolume);
            Assert.False(new PlayerSettings(Path.Combine(dir, "settings.json")).RememberVolume);
        }
    }
}
