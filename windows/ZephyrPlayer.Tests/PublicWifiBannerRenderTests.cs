using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// UI evidence for the Public-network notice on the real Player pairing panel, rendered offscreen.
    /// The Public state is a TEST HOOK: ConnectionState.PublicWifiName is set directly. This is NOT a real
    /// Windows network category change and proves nothing about how Windows classifies a real Wi-Fi.
    /// </summary>
    public class PublicWifiBannerRenderTests
    {
        private static void Render(string name, bool hookPublic)
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                var previous = ConnectionState.PublicWifiName;
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-wifi-" + Guid.NewGuid().ToString("N"));
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    ConnectionState.PublicWifiName = hookPublic ? "Hooked test Wi-Fi" : null;
                    var store = new PairedStore(System.IO.Path.Combine(dir, "paired.json"));
                    var panel = new PairingPanel(new PairingGate(), store, _ => { });
                    var window = new Window { Content = panel, Width = 600, Height = 700, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x10)) }; // the real window's AppBgBrush (Theme.xaml #0B0D10), so the banner is judged on its real backdrop
                    try
                    {
                        window.Show(); window.UpdateLayout();
                        var banner = (Border)panel.FindName("WifiBanner")!;
                        var text = (TextBlock)panel.FindName("WifiBannerText")!;
                        var button = (Button)panel.FindName("FirewallCheckButton")!;
                        Assert.Equal(hookPublic ? Visibility.Visible : Visibility.Collapsed, banner.Visibility);
                        if (hookPublic)
                        {
                            Assert.Equal("Windows is treating this Wi-Fi as public, which can block your phone from finding this PC.", text.Text);
                            Assert.Equal("Check firewall setup", button.Content);
                        }
                        var image = new RenderTargetBitmap(600, 700, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                        var outDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? System.IO.Path.GetTempPath();
                        using var output = System.IO.File.Create(System.IO.Path.Combine(outDir, name));
                        png.Save(output);
                    }
                    finally { window.Close(); }
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    ConnectionState.PublicWifiName = previous;
                    try { System.IO.Directory.Delete(dir, true); } catch { }
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start();
            Assert.True(t.Join(30000)); if (error != null) throw error;
        }

        [Fact] public void PairingPanel_ShowsPublicNetworkNotice_WhenPublicStateIsHooked() => Render("pc-pairing-publicwifi-HOOKED.png", true);
        [Fact] public void PairingPanel_HidesPublicNetworkNotice_WhenNotPublic() => Render("pc-pairing-private-HOOKED.png", false);
    }
}
