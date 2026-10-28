using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Xunit;

namespace ZephyrPlayer.Tests
{
    public class SkipPairUiTests
    {
        private static void Sta(Action<string> body)
        {
            Exception? error = null;
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-skip-pairs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var thread = new Thread(() =>
            {
                try { body(Path.Combine(dir, "settings.json")); }
                catch (Exception ex) { error = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // LibVLC init on a loaded CI runner can sit in device contention for tens of
            // seconds (several STA suites run at once); the assertion is about the layout,
            // not the clock, so the guard only catches a genuine hang.
            Assert.True(thread.Join(90000), "Skip-pair STA test did not finish");
            try { Directory.Delete(dir, true); } catch { }
            if (error != null) throw new Exception("Skip-pair UI test failed", error);
        }

        private static void Capture(FrameworkElement element, int width, string name)
        {
            element.Measure(new Size(width, 600));
            element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
            element.UpdateLayout();
            var image = new RenderTargetBitmap(width, (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var dir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
            using var stream = File.Create(Path.Combine(dir, name));
            encoder.Save(stream);
        }

        [Fact]
        public void Settings_Choices_Update_Both_Directions_And_Keep_Pairs_Independent()
        {
            Sta(path =>
            {
                var settings = new PlayerSettings(path);
                var w = new SettingsWindow(settings, new PairedStore(), () => { });
                try
                {
                    var primary = (WrapPanel)w.FindName("PrimarySkipPanel")!;
                    var secondary = (WrapPanel)w.FindName("SecondarySkipPanel")!;
                    Assert.Equal(new[] { "5s", "10s", "15s", "30s" }, primary.Children.OfType<RadioButton>().Select(b => (string)b.Content).ToArray());
                    Assert.Equal(new[] { "15s", "30s", "1m", "2m", "3m" }, secondary.Children.OfType<RadioButton>().Select(b => (string)b.Content).ToArray());
                    var p = primary.Children.OfType<RadioButton>().Single(b => Equals(b.Content, "10s"));
                    p.IsChecked = true;
                    p.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
                    Assert.Equal(-10, settings.SkipDelta(false, true));
                    Assert.Equal(10, settings.SkipDelta(false, false));
                    Assert.Equal(30, settings.SecondarySkipSeconds);
                    var s = secondary.Children.OfType<RadioButton>().Single(b => Equals(b.Content, "1m"));
                    s.IsChecked = true;
                    s.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
                    Assert.Equal(-60, settings.SkipDelta(true, true));
                    Assert.Equal(60, settings.SkipDelta(true, false));
                    Assert.Equal(10, settings.PrimarySkipSeconds);
                    // The left-nav shows one section at a time; a collapsed card
                    // measures 0 high and cannot be rendered. Open Skip first.
                    ((Button)w.FindName("NavSkip")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Capture((FrameworkElement)w.FindName("SkipSettingsCard")!, 500, "pc-skip-settings-pairs.png");
                }
                finally { w.Close(); }
            });
        }

        [Fact]
        public void Pause_Bars_Stay_Separated_And_Fullscreen_Pill_Is_Bounded()
        {
            Sta(path => {
                Core.Initialize(); using var vlc = new LibVLC(); using var player = new LibVLCSharp.Shared.MediaPlayer(vlc);
                var window = new MainWindow(vlc, player, new PlayerSettings(path), new VolumeService());
                var pause = (Grid)window.FindName("PauseGlyph")!;
                pause.Visibility = Visibility.Visible;
                pause.Measure(new Size(24,24)); pause.Arrange(new Rect(0,0,24,24)); pause.UpdateLayout();
                var a = (FrameworkElement)pause.Children[0]; var b = (FrameworkElement)pause.Children[1];
                var ar = a.TransformToAncestor(pause).TransformBounds(new Rect(0,0,a.ActualWidth,a.ActualHeight));
                var br = b.TransformToAncestor(pause).TransformBounds(new Rect(0,0,b.ActualWidth,b.ActualHeight));
                Assert.True(br.Left - ar.Right >= 6, "Pause bars merge");
                var fullscreenGlyph = (System.Windows.Shapes.Path)window.FindName("FullscreenGlyph")!;
                var enterShape = fullscreenGlyph.Data.ToString();
                window.SetFullscreen(true);
                Assert.NotEqual(enterShape, fullscreenGlyph.Data.ToString());
                Assert.Contains("Exit", window.FindName("FullscreenButton") is Button fb ? fb.ToolTip.ToString() : "");
                window.SetMuteTied(true);
                var mutedShape = ((System.Windows.Shapes.Path)window.FindName("VolumeGlyph")!).Data.ToString();
                window.SetMuteTied(false);
                Assert.NotEqual(mutedShape, ((System.Windows.Shapes.Path)window.FindName("VolumeGlyph")!).Data.ToString());
                Assert.Equal(1000, window.ControlBar.MaxWidth);
                Assert.Equal(HorizontalAlignment.Center,window.ControlBar.HorizontalAlignment);
                window.ControlBar.Opacity=1;
                Capture(window.ControlBar,1000,"pc-redesign-fullscreen-pill.png");
                window.SetFullscreen(false); Assert.Equal(HorizontalAlignment.Stretch,window.ControlBar.HorizontalAlignment);
                window.Close();
            });
        }

        [Theory]
        [InlineData(1024, false)]
        [InlineData(1024, true)]
        [InlineData(1920, true)]
        public void Four_Controls_Are_Visible_Ordered_And_Do_Not_Overlap(int width, bool extras)
        {
            Sta(path =>
            {
                Core.Initialize();
                using var libvlc = new LibVLC();
                using var player = new LibVLCSharp.Shared.MediaPlayer(libvlc);
                var settings = new PlayerSettings(path) { ShowSpeed = extras, ShowSleepTimer = extras };
                var w = new MainWindow(libvlc, player, settings, new VolumeService());
                try
                {
                    var bar = w.ControlBar;
                    bar.Opacity = 1;
                    Capture(bar, width - 48, $"pc-skip-controlbar-{width}-{extras}.png");
                    var names = new[] { "VolumeButton", "VolumeSlider", "SecondarySkipBackButton", "SkipBackButton", "PlayPauseButton", "SkipForwardButton", "SecondarySkipForwardButton", "CaptionsButton", "FullscreenButton" };
                    Rect? previous = null;
                    var center = (FrameworkElement)w.FindName("CenterPlaybackControls")!;
                    var centerRect = center.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, center.ActualWidth, center.ActualHeight));
                    Assert.True(Math.Abs((centerRect.Left + centerRect.Right) / 2 - bar.ActualWidth / 2) <= 1, "Playback controls are not centered");
                    foreach (var name in names)
                    {
                        var element = (FrameworkElement)w.FindName(name)!;
                        Assert.Equal(Visibility.Visible, element.Visibility);
                        var rect = element.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                        Assert.True(rect.Width > 0 && rect.Height > 0, name + " has no usable size");
                        Assert.True(rect.Left >= 0 && rect.Right <= bar.ActualWidth, name + " is clipped");
                        if (previous.HasValue) Assert.True(previous.Value.Right <= rect.Left + 0.5, name + " overlaps its left neighbor");
                        previous = rect;
                    }
                    Assert.Equal("10", ((TextBlock)w.FindName("SkipBackLabel")!).Text);
                    Assert.Equal("10", ((TextBlock)w.FindName("SkipForwardLabel")!).Text);
                    Assert.Equal("30", ((TextBlock)w.FindName("SecondarySkipBackLabel")!).Text);
                    Assert.Equal("30", ((TextBlock)w.FindName("SecondarySkipForwardLabel")!).Text);
                }
                finally { w.Close(); }
            });
        }
    }
}
