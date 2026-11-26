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
    /// <summary>Carried from 2.0.1: Home with a real MKV library shows real frames in its tiles and the whole path above the root.</summary>
    public class Ver201CarriedTests
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
                    var lib = Path.Combine(dir, "Users", "TestUser", "Videos", "Movies");
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
                        var w = new HomeWindow(settings, store, _ => { }, () => { }, loadDurations: true, history: new PlaybackHistory(Path.Combine(dir, "history.json"))); // isolated: never the real AppData history
                        var host = w; w.Width = 1280; w.Height = 760; w.Left = -20000; w.Top = -20000; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual;
                        w.Show();
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
                            if (!isDir) Assert.True(Equals(Prop(e, "DurationVis"), Visibility.Visible), $"no time badge for {Prop(e, "Name")} (Duration=[{Prop(e, "Duration")}], duration worker completed={w.DurationWork.IsCompleted}; worker diagnostics: [{string.Join(" | ", w.DurationDiagnostics)}])");
                        }
                        // The whole path above the library root, not "C: > ... > Movies".
                        var crumbs = (ItemsControl)w.FindName("Breadcrumb")!;
                        var texts = crumbs.Items.OfType<TextBlock>().Select(x => x.Text).Concat(crumbs.Items.OfType<Button>().Select(x => x.Content is TextBlock tb ? tb.Text : (string)x.Content)).ToList(); // crumb text: last segment is a trimming TextBlock since the ellipsis cap
                        Assert.Contains("Videos", texts);
                        Assert.Contains("TestUser", texts);
                        Assert.Equal(lib, crumbs.ToolTip as string);
                        // One phone pill, no duplicate "Pair phones".
                        Assert.Null(w.FindName("PairButton"));
                        Assert.NotNull(w.FindName("PhoneCard"));
                        // Count and sort labels (2.0.2 render 01 wording, singular where it reads better).
                        Assert.Equal("1 folder · 2 videos", ((TextBlock)w.FindName("CountText")!).Text);
                        Assert.Equal("Sort: Name", ((Button)w.FindName("SortButton")!).Content);
                        Assert.Equal("1 folder · 1 video", HomeWindow.Plural(1, "folder") + " · " + HomeWindow.Plural(1, "video"));
                        Pump(300);
                        foreach (var dpi in new[] { 96, 144, 192 }) Save((FrameworkElement)w.Content, 1280, 760, dpi, "home");
                        Assert.True(w.DurationWork.Wait(15000), "duration worker did not finish");
                        w.Close();
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
        public void Breadcrumb_WholePath_AndFoldPlan()
        {
            if (!OperatingSystem.IsWindows()) return;
            Assert.Equal(new[] { "D:" }, HomeWindow.RootPrefix(@"D:\Movies"));
            Assert.Equal(new[] { "C:", "Users", "me", "Videos" }, HomeWindow.RootPrefix(@"C:\Users\me\Videos\Movies"));
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, HomeWindow.FoldPlan(new double[] { 40, 60, 50, 70, 80 }, 30, 1000));
            Assert.Equal(new[] { 0, -1, 3, 4 }, HomeWindow.FoldPlan(new double[] { 40, 60, 50, 70, 80 }, 30, 230));
            Assert.Equal(new[] { 0, -1, 4 }, HomeWindow.FoldPlan(new double[] { 40, 60, 50, 70, 80 }, 30, 160));
        }

    }
}
