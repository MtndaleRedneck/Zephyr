using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// UI evidence without an interactive session: the pair banner is rendered
    /// offscreen (RenderTargetBitmap) and checked at the pixel level. WPF needs
    /// an STA thread; the runner has no display, but software rendering is fine.
    /// </summary>
    public class PairBannerRenderTests
    {
        [Fact]
        public void PairBanner_RendersOffscreen()
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    Core.Initialize();
                    var libvlc = new LibVLC();
                    var player = new LibVLCSharp.Shared.MediaPlayer(libvlc);
                    var w = new MainWindow(libvlc, player);
                    w.ShowPairBanner("135792");
                    Assert.Equal("1 3 5 7 9 2", w.PairCodeText.Text);
                    // Render the banner Border itself: the full window contains the
                    // libVLC VideoView, which can't composite offscreen and blanks
                    // the whole bitmap (CI-observed: all-dark then all-black PNGs).
                    // A measured/arranged plain Border renders in software fine.
                    var banner = w.PairBanner;
                    banner.Measure(new Size(1280, 720));
                    banner.Arrange(new Rect(0, 0, banner.DesiredSize.Width, banner.DesiredSize.Height));
                    var bw = (int)Math.Ceiling(banner.DesiredSize.Width);
                    var bh = (int)Math.Ceiling(banner.DesiredSize.Height);
                    var rtb = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(banner);
                    // Evidence capture is best-effort: the pixel assert is the test,
                    // the PNG only feeds the CI artifact for UI review.
                    try
                    {
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(rtb));
                        var outDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? System.IO.Path.GetTempPath();
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(outDir, "pair-banner-render.png")))
                            png.Save(fs);
                    }
                    catch (Exception ex) { Console.WriteLine("banner png capture failed: " + ex.Message); }
                    // The banner's signature, not a single opaque pixel (the old
                    // assert passed on a blank dark frame): the gold #F0C23E code
                    // text and the white title must actually appear top-center.
                    var wide = new byte[bw * bh * 4];
                    rtb.CopyPixels(new Int32Rect(0, 0, bw, bh), wide, bw * 4, 0);
                    int gold = 0, white = 0;
                    for (var i = 0; i < wide.Length; i += 4)
                    {
                        var b = wide[i]; var g = wide[i + 1]; var r = wide[i + 2];
                        if (r > 200 && g > 150 && g < 230 && b < 110) gold++;
                        if (r > 230 && g > 230 && b > 230) white++;
                    }
                    Assert.True(gold > 100, $"gold code-text pixels {gold} - banner code not rendered");
                    // Threshold encodes the settled design: "Pair with your phone" at default caption size (~69 pure-white px on the 96dpi runner render; 0-ish would mean not rendered).
                    Assert.True(white > 40, $"white title pixels {white} - banner title not rendered");
                    w.Close();
                    // Shut the dispatcher down on this STA thread: a live dispatcher at
                    // thread exit lets a zombie wndproc race host teardown (CI-observed
                    // NullReferenceException in HwndSubclass.SubclassWndProc).
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
        }
    }
    public class ControlBarRenderTests
    {
        [Fact]
        public void ControlBar_RendersOffscreen()
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    Core.Initialize();
                    var libvlc = new LibVLC();
                    var player = new LibVLCSharp.Shared.MediaPlayer(libvlc);
                    var w = new MainWindow(libvlc, player);
                    // Plain-element render only: the full window blanks offscreen
                    // (VideoView airspace, see PairBannerRenderTests).
                    var bar = w.ControlBar;
                    bar.Opacity = 1; // XAML starts it at 0 (fades in on mouse move); render the shown state
                    bar.Measure(new Size(1920, 200));
                    bar.Arrange(new Rect(0, 0, bar.DesiredSize.Width, bar.DesiredSize.Height));
                    var bw = (int)Math.Ceiling(bar.DesiredSize.Width);
                    var bh = (int)Math.Ceiling(bar.DesiredSize.Height);
                    var rtb = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(bar);
                    try
                    {
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(rtb));
                        var outDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? System.IO.Path.GetTempPath();
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(outDir, "pc-controlbar-render.png")))
                            png.Save(fs);
                    }
                    catch (Exception ex) { Console.WriteLine("control bar png capture failed: " + ex.Message); }
                    var wide = new byte[bw * bh * 4];
                    rtb.CopyPixels(new Int32Rect(0, 0, bw, bh), wide, bw * 4, 0);
                    int gold = 0, white = 0;
                    for (var i = 0; i < wide.Length; i += 4)
                    {
                        var b = wide[i]; var g = wide[i + 1]; var r = wide[i + 2];
                        if (r > 200 && g > 150 && g < 230 && b < 110) gold++;
                        if (r > 230 && g > 230 && b > 230) white++;
                    }
                    // Threshold encodes the settled 13pt captions (~44 pure-gold px on the 96dpi runner render; 0-ish would mean not rendered).
                    Assert.True(gold > 30, $"gold elapsed-text pixels {gold} - control bar not rendered");
                    Assert.True(white > 30, $"white remaining-text pixels {white} - control bar not rendered");
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
        }
    }
}
