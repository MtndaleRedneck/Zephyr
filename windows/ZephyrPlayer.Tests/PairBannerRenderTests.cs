using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
    public class PairingWindowRenderTests
    {
        [Fact]
        public void PairingWindow_RendersOffscreen()
        {
            string? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-pairui-" + Guid.NewGuid().ToString("N"));
                    System.IO.Directory.CreateDirectory(dir);
                    var store = new PairedStore(System.IO.Path.Combine(dir, "paired.json"));
                    store.Add("tok-1", "My phone");
                    var gate = new PairingGate();
                    var w = new PairingWindow(gate, store, _ => { });
                    // Loaded mints + shows a code; drive the lifecycle manually.
                    w.Show(); w.Hide();
                    w.RefreshPhones();
                    var codePanel = (UIElement)w.FindName("CodePanel")!;
                    var list = (ItemsControl)w.FindName("PhoneList")!;
                    Assert.Equal(Visibility.Visible, codePanel.Visibility);
                    Assert.Equal(1, list.Items.Count);
                    // The big code text is 6 spaced digits.
                    var codeText = ((TextBlock)w.FindName("PairCodeText")!).Text;
                    Assert.Equal(11, codeText.Length); // "1 3 5 7 9 2"
                    // Render the window content offscreen as CI evidence.
                    var root = (FrameworkElement)w.Content;
                    root.Measure(new Size(560, 600));
                    root.Arrange(new Rect(0, 0, 560, 600));
                    root.UpdateLayout();
                    var rtb = new RenderTargetBitmap(560, 600, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(root);
                    try
                    {
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(rtb));
                        var outDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? System.IO.Path.GetTempPath();
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(outDir, "pc-pairing-render.png")))
                            png.Save(fs);
                    }
                    catch (Exception ex) { Console.WriteLine("pairing png capture failed: " + ex.Message); }
                    // Pixel signature: the orange code digits must actually render.
                    var px = new byte[560 * 600 * 4];
                    rtb.CopyPixels(new Int32Rect(0, 0, 560, 600), px, 560 * 4, 0);
                    var orange = 0;
                    for (var i = 0; i < px.Length; i += 4)
                    {
                        var b = px[i]; var g = px[i + 1]; var r = px[i + 2];
                        if (r > 200 && g > 90 && g < 160 && b < 60) orange++;
                    }
                    Assert.True(orange > 150, $"orange code pixels {orange} - pairing code not rendered");
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
                    // Structural assertions, independent of pixel counts: the required
                    // elements exist with readable labels, and CC/full are vectors, not text.
                    Assert.Equal("10", ((TextBlock)w.FindName("SkipBackLabel")!).Text);
                    Assert.Equal("10", ((TextBlock)w.FindName("SkipForwardLabel")!).Text);
                    Assert.Equal("0:00", ((TextBlock)w.FindName("ElapsedText")!).Text);
                    Assert.Equal("0:00 total", ((TextBlock)w.FindName("TotalText")!).Text);
                    Assert.Equal("-0:00", ((TextBlock)w.FindName("RemainingText")!).Text);
                    foreach (var n in new[] { "VolumeButton", "PlayPauseButton", "SkipBackButton", "SkipForwardButton", "SecondarySkipBackButton", "SecondarySkipForwardButton", "CaptionsButton", "FullscreenButton", "SeekBar" })
                        Assert.Equal(Visibility.Visible, ((UIElement)w.FindName(n)!).Visibility);
                    Assert.False(((Button)w.FindName("CaptionsButton")!).Content is string, "captions must be a vector icon, not literal text");
                    Assert.False(((Button)w.FindName("FullscreenButton")!).Content is string, "fullscreen must be a vector icon, not literal text");
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
