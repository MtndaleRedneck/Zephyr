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
        public void SecondaryHighlight_ReversesSelectedColor_AndRendersBroadBand()
        {
            Sta(path=>{
                Assert.Equal(Colors.White,SecondarySkipHighlight.HighlightColor(Brand.Orange));Assert.Equal(Brand.Orange,SecondarySkipHighlight.HighlightColor(Color.FromRgb(245,245,247)));
                foreach(var color in new[]{Brand.Orange,Color.FromRgb(245,245,247)}) {
                    // The real 24-unit forward arc (render 02), drawn 8x: 192 x 192 px.
                    var canvas=new Canvas{Width=24,Height=24,Background=new SolidColorBrush(Color.FromRgb(27,27,30))};
                    var basePath=new System.Windows.Shapes.Path{Data=Geometry.Parse("M3.73,10.39 A8.8,8.8 0 0 1 20.27,10.39"),Stroke=new SolidColorBrush(color),StrokeThickness=1.8};
                    var highlight=new SecondarySkipHighlight{Width=24,Height=24};canvas.Children.Add(basePath);canvas.Children.Add(highlight);canvas.LayoutTransform=new ScaleTransform(8,8);
                    highlight.Preview(color,.5);Capture(canvas,192,"pc-secondary-highlight-"+(color==Brand.Orange?"orange":"white")+".png");
                    var pixels=new RenderTargetBitmap(192,192,96,96,PixelFormats.Pbgra32);pixels.Render(canvas);
                    var bytes=new byte[192*192*4];pixels.CopyPixels(bytes,192*4,0);
                    Color AtPixel(Point p){int x=(int)Math.Round(p.X*8),y=(int)Math.Round(p.Y*8);var at=(y*192+x)*4;return Color.FromRgb(bytes[at+2],bytes[at+1],bytes[at]);}
                    double Dist(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
                    // Band centre at the top of the arc; the tail (3 % along) is outside the band.
                    var center=AtPixel(highlight.At(.5));var outside=AtPixel(highlight.At(.03));var inverse=SecondarySkipHighlight.HighlightColor(color);
                    Assert.True(Dist(center,inverse)<Dist(center,color),"band center did not render inverse color");
                    Assert.True(Dist(outside,color)<Dist(outside,inverse),"outside band must retain selected base color");

                    highlight.Start(color);Assert.True(highlight.HasAnimatedProperties);highlight.Stop();Assert.False(highlight.HasAnimatedProperties);Assert.Equal(-2,highlight.Progress);
                }
            });
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
                var window = new PlayerView(vlc, player, new PlayerSettings(path), new VolumeService());
                var pause = (Grid)window.FindName("PauseGlyph")!;
                pause.Visibility = Visibility.Visible;
                pause.Measure(new Size(24,24)); pause.Arrange(new Rect(0,0,24,24)); pause.UpdateLayout();
                var a = (FrameworkElement)pause.Children[0]; var b = (FrameworkElement)pause.Children[1];
                var ar = a.TransformToAncestor(pause).TransformBounds(new Rect(0,0,a.ActualWidth,a.ActualHeight));
                var br = b.TransformToAncestor(pause).TransformBounds(new Rect(0,0,b.ActualWidth,b.ActualHeight));
                // Render 02: bars 2.3/24 wide at x 9 and 12.7 of the 50 px button = 2.9 px apart.
                Assert.True(br.Left - ar.Right >= 2.5, "Pause bars merge");
                var fullscreenGlyph = (System.Windows.Shapes.Path)window.FindName("FullscreenGlyph")!;
                var enterShape = fullscreenGlyph.Data.ToString();
                window.SetFullscreen(true);
                Assert.NotEqual(enterShape, fullscreenGlyph.Data.ToString());
                Assert.Contains("Exit", window.FindName("FullscreenButton") is Button fb ? fb.ToolTip.ToString() : "");
                window.SetMuteTied(true);
                var mutedShape = ((System.Windows.Shapes.Path)window.FindName("VolumeGlyph")!).Data.ToString();
                window.SetMuteTied(false);
                Assert.NotEqual(mutedShape, ((System.Windows.Shapes.Path)window.FindName("VolumeGlyph")!).Data.ToString());
                // The pill (render 03): the bar laid out at up to 1000 px and drawn at 0.78,
                // i.e. 780 px at most, and never wider than the screen minus 2 x 24 (U2).
                Assert.True(window.ControlBar.Width <= 1000.001, "pill layout wider than 1000");
                Assert.Equal(0.78, ((ScaleTransform)window.ControlBar.LayoutTransform).ScaleX, 3);
                Assert.True(window.PillDrawnWidth <= 780.001 && window.PillDrawnWidth <= SystemParameters.PrimaryScreenWidth - 48 + 0.001, "pill not clamped: " + window.PillDrawnWidth);
                Assert.Equal(HorizontalAlignment.Center,window.ControlBar.HorizontalAlignment);
                window.ControlBar.Opacity=1;
                Capture(window.ControlBar,1000,"pc-redesign-fullscreen-pill.png");
                window.SetFullscreen(false); Assert.Equal(HorizontalAlignment.Stretch,window.ControlBar.HorizontalAlignment);
                window.ReleasePlayback();
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
                var w = new PlayerView(libvlc, player, settings, new VolumeService());
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
                    Assert.Equal(Visibility.Visible, ((TextBlock)w.FindName("TotalText")!).Visibility);
                    foreach (var side in new[] { "Back", "Forward" })
                    {
                        var ring = (FrameworkElement)((System.Windows.Shapes.Path)w.FindName("Skip" + side + "Ring")!).Parent;
                        var text = (TextBlock)w.FindName("Skip" + side + "Label")!;
                        var rc = ring.TransformToAncestor(bar).Transform(new Point(ring.ActualWidth/2, ring.ActualHeight/2));
                        var tc = text.TransformToAncestor(bar).Transform(new Point(text.ActualWidth/2, text.ActualHeight/2));
                        Assert.True(Math.Abs(rc.X-tc.X)<0.5 && Math.Abs(rc.Y-tc.Y)<0.5, "Primary label not centered within ring");
                    }
                    Assert.Equal("10s", ((TextBlock)w.FindName("SkipBackLabel")!).Text);
                    Assert.Equal("10s", ((TextBlock)w.FindName("SkipForwardLabel")!).Text);
                    Assert.Equal("30s", ((TextBlock)w.FindName("SecondarySkipBackLabel")!).Text);
                    Assert.Equal("30s", ((TextBlock)w.FindName("SecondarySkipForwardLabel")!).Text);
                }
                finally { w.ReleasePlayback(); }
            });
        }
    }
}
