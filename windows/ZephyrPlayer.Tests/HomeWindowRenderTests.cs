using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// HomeWindow is constructed at app startup but had no test coverage - a
    /// ctor failure there kills the real exe before any window exists (the
    /// 1.2.0 CI capture/smoke gap). Same offscreen pattern as the pairing
    /// render test: real ctor, Show/Hide, populated-library assertions,
    /// RenderTargetBitmap evidence, pixel signature.
    /// </summary>
    public class HomeWindowRenderTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HomeWindow_RendersOffscreen_WithLibrary(bool durations)
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "zephyr-homeui-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    var lib = Path.Combine(dir, "Movies");
                    Directory.CreateDirectory(Path.Combine(lib, "Kids"));
                    File.WriteAllBytes(Path.Combine(lib, "Episode 10.mp4"), new byte[] { 1, 2, 3 });
                    File.WriteAllBytes(Path.Combine(lib, "Episode 2.mp4"), new byte[] { 1, 2, 3 });
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                    settings.LibraryFolder = lib;
                    var store = new PairedStore(Path.Combine(dir, "paired.json"));

                    var history = new PlaybackHistory(Path.Combine(dir, "history.json")); // isolated: never the real AppData history
                    var w = new HomeWindow(settings, store, _ => { }, () => { }, loadDurations: durations, history: history);
                    w.Show(); w.Hide(); // drives Loaded/Refresh like startup does

                    var emptyState = (UIElement)w.FindName("EmptyState")!;
                    var libraryView = (UIElement)w.FindName("LibraryView")!;
                    var list = (ListBox)w.FindName("MovieList")!;
                    var crumbs = (ItemsControl)w.FindName("Breadcrumb")!;
                    Assert.Equal(Visibility.Collapsed, emptyState.Visibility);
                    Assert.Equal(Visibility.Visible, libraryView.Visibility);
                    Assert.Equal(3, list.Items.Count); // Kids folder + Demo Reel.mp4
                    Assert.True(crumbs.Items.Count >= 1);

                    // Real per-file history drives the badge/progress, not a fixed mock label.
                    history.Save(Path.Combine(lib, "Episode 2.mp4"), 20000, 90000);
                    w.Refresh();
                    var entry = list.Items[1]; // natural sort after folder
                    Assert.Equal("1:30", entry.GetType().GetProperty("Duration")!.GetValue(entry));
                    Assert.Equal(Visibility.Visible, entry.GetType().GetProperty("ProgressVis")!.GetValue(entry));

                    // Empty-folder state flips correctly when nothing is set.
                    var settings2 = new PlayerSettings(Path.Combine(dir, "settings2.json"));
                    var w2 = new HomeWindow(settings2, store, _ => { }, () => { }, loadDurations: durations, history: new PlaybackHistory(Path.Combine(dir, "history2.json")));
                    w2.Show(); w2.Hide();
                    Assert.Equal(Visibility.Visible, ((UIElement)w2.FindName("EmptyState")!).Visibility);
                    w2.Close();

                    // Render offscreen as CI evidence.
                    var root = (FrameworkElement)w.Content;
                    root.Measure(new Size(940, 700));
                    root.Arrange(new Rect(0, 0, 940, 700));
                    root.UpdateLayout();
                    var rtb = new RenderTargetBitmap(940, 700, 96, 96, PixelFormats.Pbgra32);
                    var bg = new DrawingVisual();
                    using (var dc = bg.RenderOpen()) dc.DrawRectangle(w.Background, null, new Rect(0, 0, 940, 700));
                    rtb.Render(bg); // window chrome background sits behind the content, as on screen
                    rtb.Render(root);
                    try
                    {
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(rtb));
                        var outDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
                        using var fs = File.Create(Path.Combine(outDir, "pc-home-render.png"));
                        png.Save(fs);
                    }
                    catch (Exception ex) { Console.WriteLine("home png capture failed: " + ex.Message); }
                    // Pixel signature: not a dead black window - toolbar/tiles/text rendered.
                    var px = new byte[940 * 700 * 4];
                    rtb.CopyPixels(new Int32Rect(0, 0, 940, 700), px, 940 * 4, 0);
                    var lit = 0;
                    for (var i = 0; i < px.Length; i += 4)
                    {
                        if (px[i] > 60 || px[i + 1] > 60 || px[i + 2] > 60) lit++;
                    }
                    var bgAt = (600 * 940 + 600) * 4; // B,G,R,A: exactly the intended AppBgBrush #0B0D10, opaque (not transparent/black by accident)
                    Assert.True(px[bgAt] == 0x10 && px[bgAt + 1] == 0x0D && px[bgAt + 2] == 0x0B && px[bgAt + 3] == 0xFF, $"window fill is {px[bgAt + 2]:X2}{px[bgAt + 1]:X2}{px[bgAt]:X2}/{px[bgAt + 3]:X2}, expected 0B0D10 opaque");
                    Assert.True(lit > 4000, $"lit pixels {lit} - home grid not rendered");
                    // No horizontal overflow: a single long folder name (this fixture's 36-char
                    // temp-dir segment) must ellipsize, never push the row's right-hand controls
                    // past the window edge. Grounded in the 940px render that cut them off.
                    foreach (var name in new[] { "CommandRight", "PhoneCard" })
                    {
                        var el = (FrameworkElement)w.FindName(name)!;
                        var r = el.TransformToAncestor(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                        // Pinned viewport: the literal arrange width, never a content-grown root.
                        Assert.True(r.Right <= 940 + 0.5, name + $" overflows the 940px viewport: right edge {r.Right:F1}");
                    }

                    // Long END segment: a library root whose own name is 40 chars, one file inside.
                    var libLong = Path.Combine(dir, "A very long library folder name 0123456789AB");
                    Directory.CreateDirectory(libLong);
                    File.WriteAllBytes(Path.Combine(libLong, "Clip.mp4"), new byte[] { 1, 2, 3 });
                    var settings3 = new PlayerSettings(Path.Combine(dir, "settings3.json")) { LibraryFolder = libLong };
                    var w3 = new HomeWindow(settings3, store, _ => { }, () => { }, loadDurations: false,
                        history: new PlaybackHistory(Path.Combine(dir, "history3.json")));
                    w3.Show(); w3.Hide();
                    var root3 = (FrameworkElement)w3.Content;
                    root3.Measure(new Size(940, 700));
                    root3.Arrange(new Rect(0, 0, 940, 700));
                    root3.UpdateLayout();
                    foreach (var name in new[] { "CommandRight", "PhoneCard" })
                    {
                        var el = (FrameworkElement)w3.FindName(name)!;
                        var r = el.TransformToAncestor(root3).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                        Assert.True(r.Right <= 940 + 0.5, name + $" overflows the 940px viewport with a long end segment: right edge {r.Right:F1}");
                    }
                    Assert.True(w3.DurationWork.Wait(10000), "duration worker did not finish");
                    w3.Close();
                    // Finish native metadata work before tearing down its owning STA.
                    Assert.True(w.DurationWork.Wait(10000), "duration worker did not finish");
                    w.Close();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                    try { Directory.Delete(dir, true); } catch { } // temp fixtures only
                }
                catch (Exception e) { error = e.ToString(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(30000), "STA thread did not finish");
            Assert.True(error == null, error);
        }

        [Fact]
        public void HomeWindow_Narrow_Shows_Three_Continue_Items_And_The_Watched_Badge()
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "zephyr-homenarrow-" + Guid.NewGuid().ToString("N"));
                    var lib = Path.Combine(dir, "Movies");
                    Directory.CreateDirectory(Path.Combine(lib, ".Watched"));
                    Directory.CreateDirectory(Path.Combine(lib, "Kids"));
                    var history = new PlaybackHistory(Path.Combine(dir, "history.json"));
                    foreach (var name in new[] { "A long movie title that keeps going.mp4", "Second movie.mp4", "Third movie.mp4" })
                    {
                        var f = Path.Combine(lib, name);
                        File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
                        history.Save(f, 20000, 90000);
                    }
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json")) { LibraryFolder = lib };
                    var w = new HomeWindow(settings, new PairedStore(Path.Combine(dir, "paired.json")), _ => { }, () => { }, loadDurations: false, history: history);
                    w.Show(); w.Hide();
                    var list = (ListBox)w.FindName("MovieList")!;
                    var watched = false; var kids = false;
                    foreach (var e in list.Items)
                    {
                        var type = e.GetType();
                        var name = (string)type.GetProperty("Name")!.GetValue(e)!;
                        var vis = (Visibility)type.GetProperty("WatchedVis")!.GetValue(e)!;
                        if (name == ".Watched") { watched = true; Assert.Equal(Visibility.Visible, vis); }
                        if (name == "Kids") { kids = true; Assert.Equal(Visibility.Collapsed, vis); }
                    }
                    Assert.True(watched && kids, "both folders listed");
                    var cont = (ItemsControl)w.FindName("ContinueList")!;
                    Assert.Equal(3, cont.Items.Count); // all three seeded unfinished videos, not just rendered names
                    Assert.Equal(Visibility.Visible, ((UIElement)w.FindName("ContinueStrip")!).Visibility);
                    var root = (FrameworkElement)w.Content;
                    // Lay the window out at the real minimum size first, so nothing keeps a wider cached layout.
                    w.Width = 900; w.Height = 600; w.Show(); w.UpdateLayout();
                    root.InvalidateMeasure(); root.Measure(new Size(900, 600)); root.Arrange(new Rect(0, 0, 900, 600)); root.UpdateLayout();
                    var phoneChip = (FrameworkElement)w.FindName("PhoneCard")!;
                    var chipRight = phoneChip.TranslatePoint(new Point(phoneChip.ActualWidth, 0), root).X;
                    Assert.True(chipRight <= 900, $"phone chip clipped at 900 wide: right edge {chipRight}");
                    Assert.True(list.ActualWidth <= 900, $"movie list wider than the window: {list.ActualWidth}");
                    var rtb = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32);
                    var bg = new DrawingVisual();
                    using (var dc = bg.RenderOpen()) dc.DrawRectangle(w.Background, null, new Rect(0, 0, 900, 600));
                    rtb.Render(bg); rtb.Render(root);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "pc-home-narrow.png"))) png.Save(fs);
                    w.Close();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                    try { Directory.Delete(dir, true); } catch { } // temp fixtures only
                }
                catch (Exception e) { error = e.ToString(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(30000), "STA thread did not finish");
            Assert.True(error == null, error);
        }

        [Fact]
        public void HomeSearch_FocusBorder_Can_Win_Over_The_Base_Border()
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "zephyr-homesearch-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                    var w = new HomeWindow(settings, new PairedStore(Path.Combine(dir, "paired.json")), _ => { }, () => { }, loadDurations: false, history: new PlaybackHistory(Path.Combine(dir, "h.json")));
                    w.Show(); w.Hide();
                    var box = (FrameworkElement)w.FindName("SearchBox")!;
                    var border = box.Parent is Grid g && g.Parent is Border b ? b : throw new InvalidOperationException("search layout changed");
                    // WPF precedence: a locally set BorderBrush would beat the style's focus trigger. The base brush must come from the style.
                    Assert.NotEqual(BaseValueSource.Local, DependencyPropertyHelper.GetValueSource(border, Border.BorderBrushProperty).BaseValueSource);
                    // The style carries a trigger on the box's keyboard focus that sets the orange border.
                    var dt = border.Style.Triggers.OfType<DataTrigger>().Single();
                    Assert.Contains(dt.Setters.OfType<Setter>(), x => x.Property == Border.BorderBrushProperty);
                    w.Close();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                    try { Directory.Delete(dir, true); } catch { }
                }
                catch (Exception e) { error = e.ToString(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(30000), "STA thread did not finish");
            Assert.True(error == null, error);
        }
    }
}
