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
                    // CP4 item 88: the one pairing surface is Settings > Paired phones (the panel).
                    var w = new PairingPanel(gate, store, _ => { });
                    var host = new Window { Content = w, Width = 600, Height = 700, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x15)), ShowInTaskbar = false };
                    host.Show();
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
                    Assert.Equal(11, codeText.Length); // "1 3 5 7 9 2" with thin spaces, word space in the middle
                    Assert.Equal(' ', codeText[5]);
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
                    host.Close();
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
                    // Explicit "original" pick (also the shipping default again, render 06b):
                    // keep the render check independent of which style ships as the default.
                    var settings = new PlayerSettings(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-controlbar-" + Guid.NewGuid().ToString("N") + ".json"));
                    settings.BarColorStyle = "original";
                    var w = new PlayerView(libvlc, player, settings, new VolumeService());
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
                    Assert.True(gold > 30, $"gold (secondary arrows / Play) pixels {gold} - control bar not rendered");
                    Assert.True(white > 30, $"white (primary rings / labels) pixels {white} - control bar not rendered");
                    w.ReleasePlayback();

                    // The SHIPPING default (no pick stored) = "original" (render 02): orange
                    // secondary arrows and Play on the bar. Same offscreen render, orange
                    // pixel count - visual proof of the default, not just a brush assert.
                    var settingsDefault = new PlayerSettings(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-controlbar-def-" + Guid.NewGuid().ToString("N") + ".json"));
                    Assert.Equal("original", settingsDefault.BarColorStyle); // unset default
                    var w2 = new PlayerView(libvlc, player, settingsDefault, new VolumeService());
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
                    Assert.True(orangePx > 30, $"orange pixels {orangePx} - default control bar not rendered in the shipping style");
                    w2.ReleasePlayback();
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

    public class Cp3PairedPhonesTests
    {
        private static void Sta(Action body)
        {
            Exception? error = null;
            var t = new Thread(() => { try { body(); } catch (Exception e) { error = e; } finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
            t.SetApartmentState(ApartmentState.STA); t.Start();
            Assert.True(t.Join(30000), "STA body timed out");
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

        [Fact]
        public void FormatCode_ThinSpacesAndOneWordSpace()
        {
            Assert.Equal("4\u20098\u20092 9\u20091\u20093", PairingPanel.FormatCode("482913"));
            Assert.Equal(11, PairingPanel.FormatCode("135792").Length);
            Assert.Equal("4\u20098\u20092\u20099", PairingPanel.FormatCode("4829"));
        }

        [Fact]
        public void PhoneStatus_RenderWording()
        {
            var now = new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Local);
            Assert.Equal("Connected now", PairingPanel.PhoneStatus(true, null, now));
            Assert.Equal("Not connected this session", PairingPanel.PhoneStatus(false, null, now));
            Assert.StartsWith("Last seen today, ", PairingPanel.PhoneStatus(false, now.AddHours(-2).ToUniversalTime(), now));
            Assert.Equal("Last seen yesterday", PairingPanel.PhoneStatus(false, now.AddDays(-1).ToUniversalTime(), now));
            Assert.StartsWith("Last seen Oct", PairingPanel.PhoneStatus(false, now.AddDays(-5).ToUniversalTime(), now));
        }

        [Fact]
        public void PhoneCard_Forget_RemovesThePhone()
        {
            Sta(() =>
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-forget-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(dir);
                var store = new PairedStore(System.IO.Path.Combine(dir, "paired.json"));
                store.Add("tok-a", "Kitchen phone");
                store.Add("tok-b", "Ezzy's phone");
                var panel = new PairingPanel(new PairingGate(), store, _ => { });
                var host = new Window { Content = panel, Width = 640, Height = 600, ShowInTaskbar = false };
                host.Show(); host.UpdateLayout();
                Assert.Equal(2, ((ItemsControl)panel.FindName("PhoneList")!).Items.Count);
                var forget = Find<Button>(panel, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Forget:Kitchen phone");
                Assert.NotNull(forget);
                Assert.Equal("Forget", forget!.Content);
                forget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                host.UpdateLayout();
                Assert.Single(store.Entries());
                Assert.Equal("Ezzy's phone", store.Entries()[0].Name);
                Assert.Equal(1, ((ItemsControl)panel.FindName("PhoneList")!).Items.Count);
                // Survives a reload from disk.
                Assert.Single(new PairedStore(System.IO.Path.Combine(dir, "paired.json")).Entries());
                var last = Find<Button>(panel, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Forget:Ezzy's phone");
                last!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                host.UpdateLayout();
                Assert.Empty(store.Entries());
                Assert.Equal(Visibility.Visible, ((TextBlock)panel.FindName("NoPhonesText")!).Visibility);
                host.Close();
            });
        }

        [Fact]
        public void PublicWifiBanner_NoticeOnly_AndCheckIsReadOnly()
        {
            Sta(() =>
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-wifi-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(dir);
                var store = new PairedStore(System.IO.Path.Combine(dir, "paired.json"));
                var before = ConnectionState.PublicWifiName;
                try
                {
                    ConnectionState.PublicWifiName = "CoffeeShop";
                    var panel = new PairingPanel(new PairingGate(), store, _ => { });
                    var host = new Window { Content = panel, Width = 640, Height = 600, ShowInTaskbar = false };
                    host.Show(); host.UpdateLayout();
                    var banner = (Border)panel.FindName("WifiBanner")!;
                    Assert.Equal(Visibility.Visible, banner.Visibility);
                    var text = ((TextBlock)panel.FindName("WifiBannerText")!).Text;
                    Assert.Contains("public", text);
                    Assert.Contains("can block", text);       // honest: may, not "is blocked"
                    Assert.DoesNotContain("is blocked", text);
                    string? said = null;
                    Action<string> h = s => said = s;
                    PairingPanel.SuppressDialogsForTest = true;
                    PairingPanel.FirewallCheckResult += h;
                    try { ((Button)panel.FindName("FirewallCheckButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    finally { PairingPanel.FirewallCheckResult -= h; PairingPanel.SuppressDialogsForTest = false; }
                    Assert.NotNull(said);
                    Assert.DoesNotContain("shut", said!, StringComparison.OrdinalIgnoreCase);
                    ConnectionState.PublicWifiName = null;
                    panel.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    host.Close();
                    var panel2 = new PairingPanel(new PairingGate(), store, _ => { });
                    var host2 = new Window { Content = panel2, Width = 640, Height = 600, ShowInTaskbar = false };
                    host2.Show(); host2.UpdateLayout();
                    Assert.Equal(Visibility.Collapsed, ((Border)panel2.FindName("WifiBanner")!).Visibility);
                    host2.Close();
                }
                finally { ConnectionState.PublicWifiName = before; }
            });
        }
    }
}
