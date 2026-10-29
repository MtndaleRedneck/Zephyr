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
        public void BrokenPairingRecovery_RendersExplicitResetWithoutChangingStore()
        {
            Exception? error=null;var thread=new Thread(()=>{
                var dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(dir);
                try{
                    var path=System.IO.Path.Combine(dir,"paired.json");System.IO.File.WriteAllText(path,"broken{");var store=new PairedStore(path);_=store.Entries();
                    var gate=new PairingGate();var panel=new PairingPanel(gate,store,_=>{});var window=new Window{Content=panel,Width=600,Height=700};
                    try{
                        window.Show();window.UpdateLayout();
                        Assert.Equal(Visibility.Visible,((Button)panel.FindName("ResetPairingsButton")!).Visibility);
                        Assert.False(((Button)panel.FindName("PairButton")!).IsEnabled);
                        var image=new RenderTargetBitmap(600,700,96,96,PixelFormats.Pbgra32);image.Render(window);
                        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
                        using var output=System.IO.File.Create(System.IO.Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP")??System.IO.Path.GetTempPath(),"pc-pairing-recovery-render.png"));png.Save(output);
                        Assert.Equal("broken{",System.IO.File.ReadAllText(path));Assert.True(store.IsBroken);
                    }finally{window.Close();}
                }catch(Exception ex){error=ex;}finally{System.IO.Directory.Delete(dir,true);System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}
            });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(30000));if(error!=null)throw error;
        }
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
                    Assert.Equal(Visibility.Collapsed, codePanel.Visibility);
                    Assert.False(gate.HasActiveCode);
                    var pair = (Button)w.FindName("PairButton")!;
                    pair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(gate.HasActiveCode); // empty Pair focuses, never mints
                    ((TextBox)w.FindName("PhoneNameBox")!).Text = "Test phone";
                    pair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Test phone", ((TextBox)w.FindName("PhoneNameBox")!).Text);
                    Assert.True(gate.HasActiveCode);
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
                    // Explicit "original" pick: the shipping default is orange now, and this
                    // render proof's pixel calibration below is gold. Keep the render check
                    // independent of which style ships as the default.
                    var settings = new PlayerSettings(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-controlbar-" + Guid.NewGuid().ToString("N") + ".json"));
                    settings.BarColorStyle = "original";
                    var w = new MainWindow(libvlc, player, settings, new VolumeService());
                    // Plain-element render only: the full window blanks offscreen
                    // (VideoView airspace, see PairBannerRenderTests).
                    var bar = w.ControlBar;
                    // Structural assertions, independent of pixel counts: the required
                    // elements exist with readable labels, and CC/full are vectors, not text.
                    Assert.Equal("10s", ((TextBlock)w.FindName("SkipBackLabel")!).Text);
                    Assert.Equal("10s", ((TextBlock)w.FindName("SkipForwardLabel")!).Text);
                    Assert.Equal("0:00", ((TextBlock)w.FindName("ElapsedText")!).Text);
                    Assert.Equal("0:00 total", ((TextBlock)w.FindName("TotalText")!).Text);
                    Assert.Equal("-0:00", ((TextBlock)w.FindName("RemainingText")!).Text);
                    foreach (var n in new[] { "VolumeButton", "PlayPauseButton", "SkipBackButton", "SkipForwardButton", "SecondarySkipBackButton", "SecondarySkipForwardButton", "CaptionsButton", "FullscreenButton", "SeekBar" })
                        Assert.Equal(Visibility.Visible, ((UIElement)w.FindName(n)!).Visibility);
                    Assert.False(((Button)w.FindName("CaptionsButton")!).Content is string, "captions must be a vector icon, not literal text");
                    Assert.False(((Button)w.FindName("FullscreenButton")!).Content is string, "fullscreen must be a vector icon, not literal text");
                    bar.Opacity = 1; // XAML starts it at 0 (fades in on mouse move); render the shown state
                    bar.Measure(new Size(800, 200));
                    bar.Arrange(new Rect(0, 0, 800, bar.DesiredSize.Height));
                    w.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.SystemIdle);
                    bar.UpdateLayout();
                    var bw = 800;
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
                        if (r > 200 && g > 90 && g < 180 && b < 110) gold++;
                        if (r > 230 && g > 230 && b > 230) white++;
                    }
                    // Threshold encodes the settled 13pt captions (~44 pure-gold px on the 96dpi runner render; 0-ish would mean not rendered).
                    Assert.True(gold > 30, $"gold elapsed-text pixels {gold} - control bar not rendered");
                    Assert.True(white > 30, $"white remaining-text pixels {white} - control bar not rendered");
                    w.Close();

                    // The SHIPPING default (no pick stored): the bar must render with the
                    // orange elapsed/total/remaining text. Same offscreen render, orange
                    // pixel count - visual proof of the default, not just a brush assert.
                    var settingsDefault = new PlayerSettings(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-controlbar-def-" + Guid.NewGuid().ToString("N") + ".json"));
                    Assert.Equal("orange", settingsDefault.BarColorStyle); // unset default
                    var w2 = new MainWindow(libvlc, player, settingsDefault, new VolumeService());
                    var bar2 = w2.ControlBar;
                    bar2.Opacity = 1;
                    bar2.Measure(new Size(800, 200));
                    bar2.Arrange(new Rect(0, 0, 800, bar2.DesiredSize.Height));
                    w2.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.SystemIdle);
                    bar2.UpdateLayout();
                    var bw2 = 800;
                    var bh2 = (int)Math.Ceiling(bar2.DesiredSize.Height);
                    var rtb2 = new RenderTargetBitmap(bw2, bh2, 96, 96, PixelFormats.Pbgra32);
                    rtb2.Render(bar2);
                    try
                    {
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(rtb2));
                        var outDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? System.IO.Path.GetTempPath();
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(outDir, "pc-controlbar-render-orange.png")))
                            png.Save(fs);
                    }
                    catch (Exception ex) { Console.WriteLine("orange control bar png capture failed: " + ex.Message); }
                    var wide2 = new byte[bw2 * bh2 * 4];
                    rtb2.CopyPixels(new Int32Rect(0, 0, bw2, bh2), wide2, bw2 * 4, 0);
                    int orangePx = 0;
                    for (var i = 0; i < wide2.Length; i += 4)
                    {
                        var b = wide2[i]; var g = wide2[i + 1]; var r = wide2[i + 2];
                        if (r > 220 && g > 110 && g < 190 && b < 80) orangePx++; // brand orange #FF9500
                    }
                    Assert.True(orangePx > 30, $"orange elapsed-text pixels {orangePx} - default control bar not rendered in the shipping style");
                    w2.Close();
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
