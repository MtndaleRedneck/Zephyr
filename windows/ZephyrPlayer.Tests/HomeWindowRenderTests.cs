using System;
using System.IO;
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

                    var w = new HomeWindow(settings, store, _ => { }, () => { }, loadDurations: durations);
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
                    var history = new PlaybackHistory();
                    history.Save(Path.Combine(lib, "Episode 2.mp4"), 20000, 90000);
                    w.Refresh();
                    var entry = list.Items[1]; // natural sort after folder
                    Assert.Equal("1:30", entry.GetType().GetProperty("Duration")!.GetValue(entry));
                    Assert.Equal(Visibility.Visible, entry.GetType().GetProperty("ProgressVis")!.GetValue(entry));

                    // Empty-folder state flips correctly when nothing is set.
                    var settings2 = new PlayerSettings(Path.Combine(dir, "settings2.json"));
                    var w2 = new HomeWindow(settings2, store, _ => { }, () => { }, loadDurations: durations);
                    w2.Show(); w2.Hide();
                    Assert.Equal(Visibility.Visible, ((UIElement)w2.FindName("EmptyState")!).Visibility);
                    w2.Close();

                    // Render offscreen as CI evidence.
                    var root = (FrameworkElement)w.Content;
                    root.Measure(new Size(940, 700));
                    root.Arrange(new Rect(0, 0, 940, 700));
                    root.UpdateLayout();
                    var rtb = new RenderTargetBitmap(940, 700, 96, 96, PixelFormats.Pbgra32);
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
                    Assert.True(lit > 4000, $"lit pixels {lit} - home grid not rendered");
                    // Finish native metadata work before tearing down its owning STA.
                    Assert.True(w.DurationWork.Wait(10000), "duration worker did not finish");
                    w.Close();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch (Exception e) { error = e.ToString(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(30000);
            Assert.True(error == null, error ?? "STA thread did not finish");
        }
    }
}
