using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Carried from 2.0.2 RedesignRender (Settings_Nav_Titles_Version, Settings_LivePreview_FollowsSettings),
    /// adapted to the 3.0 Settings (section titles of the 3.0 nav (dormant Movies folder section removed in the Midnight Slate rebuild; the folder picker lives on Home), "v3.0.0" version line,
    /// PlaybackPreview as the live preview). Same intent: every nav entry shows its title, the version line is the
    /// real assembly version, an unknown section does not switch, and the preview follows each setting.
    /// </summary>
    public class SettingsCarriedTests
    {
        private static void Sta(Action body)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { error = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(60000), "STA thread did not finish");
            Assert.Null(error);
        }

        private static SettingsWindow NewWindow(out PlayerSettings settings)
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-set-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
            return new SettingsWindow(settings, new PairedStore(Path.Combine(dir, "pairs.json")), () => { });
        }

        [Fact]
        public void Settings_Nav_Titles_Version()
        {
            Sta(() =>
            {
                var window = NewWindow(out _);
                Assert.Equal("Zephyr Settings", window.Title);   // 2.0.2 title assert, 3.0 window
                var titles = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "volume", "Volume" }, { "skip", "Playback" }, { "bar", "Controls" },
                    { "remote", "Phones" }, { "feedback", "Feedback & bugs" }, { "updates", "Updates" },
                };
                foreach (var kv in titles)
                {
                    window.SelectSection(kv.Key);
                    Assert.Equal(kv.Value, ((TextBlock)window.FindName("SectionTitle")!).Text);
                }
                var v = typeof(SettingsWindow).Assembly.GetName().Version!;
                Assert.Equal("v" + v.ToString(3), ((TextBlock)window.FindName("NavVersionText")!).Text);
                window.SelectSection("volume");
                var boost = (WrapPanel)window.FindName("BoostPanel")!;
                Assert.Equal(PlayerSettings.BoostChoices.Length, boost.Children.Count);
                window.Close();
            });
        }

        [Fact]
        public void Settings_LivePreview_FollowsSettings()
        {
            Sta(() =>
            {
                var window = NewWindow(out var settings);
                window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Show();
                void Pump() => window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Pump();
                var preview = (PlaybackPreview)((ContentControl)window.FindName("BarPreview")!).Content;
                string Text(string name) => ((TextBlock)preview.FindName(name)!).Text;
                Color Ring(string name) => ((SolidColorBrush)((System.Windows.Shapes.Shape)preview.FindName(name)!).Stroke).Color;
                var white = Color.FromRgb(0xF0, 0xF2, 0xF5);
                Assert.Equal("10s", Text("SkipBackLabel"));
                Assert.Equal("30s", Text("SecondarySkipBackLabel"));
                Assert.Equal(white, Ring("SkipBackRing"));
                settings.PrimarySkipSeconds = 15; settings.SecondarySkipSeconds = 60; Pump(); Pump();
                Assert.Equal("15s", Text("SkipBackLabel"));
                Assert.Equal("1m", Text("SecondarySkipForwardLabel"));
                settings.BarColorStyle = "orange"; Pump(); Pump();
                Assert.Equal(Brand.Orange, Ring("SkipBackRing"));
                settings.BarColorStyle = "twotone"; Pump(); Pump();
                Assert.Equal(Brand.Orange, Ring("SkipBackRing"));
                Assert.Equal(white, ((SolidColorBrush)((TextBlock)preview.FindName("SkipBackLabel")!).Foreground).Color);
                settings.IconStyle = "filled"; Pump(); Pump();
                Assert.Equal(Brand.Orange, ((SolidColorBrush)((System.Windows.Shapes.Shape)preview.FindName("PlayRing")!).Fill).Color);
                settings.ShowSpeed = false; settings.ShowSleepTimer = false; Pump(); Pump();
                Assert.Equal(Visibility.Collapsed, ((UIElement)preview.FindName("SpeedButton")!).Visibility);
                Assert.Equal(Visibility.Collapsed, ((UIElement)preview.FindName("SleepButton")!).Visibility);
                // A chip click writes the setting (and so the preview).
                window.SelectSection("skip"); window.UpdateLayout();
                var chips = (WrapPanel)window.FindName("PrimarySkipPanel")!;
                var five = (RadioButton)chips.Children[0];
                five.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(); Pump();
                Assert.Equal(5, settings.PrimarySkipSeconds);
                Assert.Equal("5s", Text("SkipBackLabel"));
                window.Close();
            });
        }
    }
}
