using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
namespace ZephyrPlayer {
 public partial class PlaybackPreview : UserControl {
  public PlaybackPreview(PlayerSettings settings) { InitializeComponent(); Update(settings); }
  public void Update(PlayerSettings settings) {
   var orange = new SolidColorBrush(Brand.Orange); var white = Brushes.WhiteSmoke;
   var ring = settings.BarColorStyle is "orange" or "twotone" ? orange : white;
   var glyph = settings.BarColorStyle is "orange" or "inverse" ? orange : white;
   SkipBackRing.Stroke = SkipForwardRing.Stroke = ring; SkipBackArrow.Fill = SkipForwardArrow.Fill = ring;
   SecondarySkipBackIcon.Stroke = SecondarySkipForwardIcon.Stroke = ring;
   SkipBackLabel.Text = SkipForwardLabel.Text = PlayerSettings.FormatSkip(settings.PrimarySkipSeconds);
   SecondarySkipBackLabel.Text = SecondarySkipForwardLabel.Text = PlayerSettings.FormatSkip(settings.SecondarySkipSeconds);
   SkipBackLabel.Foreground = SkipForwardLabel.Foreground = SecondarySkipBackLabel.Foreground = SecondarySkipForwardLabel.Foreground = glyph;
   PlayRing.Stroke = ring; PlayRing.Fill = settings.IconStyle == "filled" ? ring : Brushes.Transparent;
   PlayGlyph.Fill = settings.IconStyle == "filled" ? Brushes.Black : ring;
   VolumeButton.Foreground = SpeedButton.Foreground = SleepButton.Foreground = CaptionsButton.Foreground = FullscreenButton.Foreground = glyph;
   SpeedButton.Visibility = settings.ShowSpeed ? Visibility.Visible : Visibility.Collapsed; SleepButton.Visibility = settings.ShowSleepTimer ? Visibility.Visible : Visibility.Collapsed;
   ElapsedText.Text = "0:41:12"; RemainingText.Text = "-1:00:56"; TotalText.Text = "1:42:08 total"; SeekBar.Value = 400; VolumeSlider.Value = 62;
  }
 }
}