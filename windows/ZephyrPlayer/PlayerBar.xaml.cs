using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ZephyrPlayer
{
    /// <summary>
    /// The player's control bar (render 02) as its own control (CP3 item 73). The
    /// player view hosts one over the video and drives it; Settings hosts a second
    /// instance in preview mode as the LIVE PREVIEW on the Skip buttons and Playback
    /// bar tabs. Both are styled by the same <see cref="ApplyStyle"/>, so the preview
    /// is the real bar, not a copy (the old 95-line PlaybackPreview is deleted).
    /// </summary>
    public partial class PlayerBar : UserControl
    {
        internal static readonly SolidColorBrush RingDefault = new(Color.FromRgb(0xF5, 0xF5, 0xF7));
        internal static readonly SolidColorBrush IconDefault = new(Color.FromRgb(0xE9, 0xE9, 0xEE));
        internal static readonly SolidColorBrush Orange = new(Brand.Orange);
        internal static readonly SolidColorBrush OrangeTint = new(Color.FromArgb(0x1A, 0xFF, 0x95, 0x00));
        internal static readonly SolidColorBrush WhiteTint = new(Color.FromArgb(0x1A, 0xF5, 0xF5, 0xF7));
        internal static readonly SolidColorBrush Dark = new(Color.FromRgb(0x14, 0x14, 0x16));

        // Shared app-wide; frozen so any STA thread may use them (tests run each case
        // on its own STA thread).
        static PlayerBar()
        {
            RingDefault.Freeze(); IconDefault.Freeze(); Orange.Freeze();
            OrangeTint.Freeze(); WhiteTint.Freeze(); Dark.Freeze();
        }

        public PlayerBar()
        {
            InitializeComponent();
        }

        /// <summary>True for the Settings instance: shown, not interactive, sample values.</summary>
        public bool IsPreview { get; private set; }

        /// <summary>Settings' LIVE PREVIEW: the same bar, always shown, laid out flat (no
        /// outer inset), not interactive, with the render's sample values (render 06 /
        /// 06b: 0:41:12 of 1:42:08, volume 62 %). Styling comes from [settings] through
        /// <see cref="ApplyStyle"/>, exactly as on the player.</summary>
        public void ShowAsPreview(PlayerSettings settings)
        {
            IsPreview = true;
            IsHitTestVisible = false;
            Focusable = false;
            ControlBar.Opacity = 1;
            ControlBar.Margin = new Thickness(0);
            ControlBar.VerticalAlignment = VerticalAlignment.Center;
            ElapsedText.Text = "0:41:12";
            RemainingText.Text = "-1:00:56";
            TotalText.Text = "1:42:08 total";
            SeekBar.Maximum = 1000;
            SeekBar.Value = 405;
            VolumeSlider.Value = 62;
            VolumeLabel.Text = "62%";
            ApplyStyle(settings);
        }

        // ---------- styling (Settings, live; items 58, 73, 75) ----------

        internal static void ApplyGlyphBrush(DependencyObject element, Brush brush)
        {
            if (element is System.Windows.Shapes.Path path)
            {
                if (path.Stroke != null) path.Stroke = brush;
                if (path.Fill != null) path.Fill = brush;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyGlyphBrush(VisualTreeHelper.GetChild(element, i), brush);
            if (element is ContentControl cc && cc.Content is DependencyObject content && VisualTreeHelper.GetChildrenCount(element) == 0) ApplyGlyphBrush(content, brush);
            if (element is Panel panel && VisualTreeHelper.GetChildrenCount(element) == 0) foreach (UIElement child in panel.Children) ApplyGlyphBrush(child, brush);
            if (element is Decorator dec && dec.Child != null && VisualTreeHelper.GetChildrenCount(element) == 0) ApplyGlyphBrush(dec.Child, brush);
        }

        /// <summary>"10s" with a smaller unit letter, as render 02's skip glyphs. The
        /// TextBlock's Text still reads "10s".</summary>
        internal static void SetSkipLabel(TextBlock tb, int seconds, double size)
        {
            var text = PlayerSettings.FormatSkip(seconds);
            var unitAt = text.Length - 1;
            // Text first (TextBlock.Text does not follow later Inline edits, CI run
            // 41047779027), then the same characters as two runs for the drawing.
            tb.Text = text;
            tb.Inlines.Clear();
            tb.Inlines.Add(new Run(text.Substring(0, unitAt)) { FontSize = size });
            tb.Inlines.Add(new Run(text.Substring(unitAt)) { FontSize = Math.Round(size * 0.72, 1), FontWeight = FontWeights.SemiBold });
        }

        internal void ApplySkipLabels(PlayerSettings settings)
        {
            var pri = (SkipBackBox.Width) / 42.0 * 12.6;
            var sec = (SecondaryBackBox.Width) / 40.0 * 13.0;
            SetSkipLabel(SkipBackLabel, settings.PrimarySkipSeconds, pri);
            SetSkipLabel(SkipForwardLabel, settings.PrimarySkipSeconds, pri);
            SetSkipLabel(SecondarySkipBackLabel, settings.SecondarySkipSeconds, sec);
            SetSkipLabel(SecondarySkipForwardLabel, settings.SecondarySkipSeconds, sec);
        }

        /// <summary>The five colour sets (render 06b; "Default is the original") and
        /// Outline/Solid. Original = render 02: white primary rings and labels, orange
        /// secondary arrows and labels, orange Play. Orange / White = everything in that
        /// colour. "White, orange rings" = orange rings, white labels. "Orange, white rings"
        /// = white rings, orange labels. Returns the brush for the plain icons.</summary>
        internal Brush ApplyStyle(PlayerSettings settings)
        {
            var style = settings.BarColorStyle;
            Brush priRing, priLabel, secRing, secLabel, play, icons;
            switch (style)
            {
                case "orange": priRing = priLabel = secRing = secLabel = play = Orange; icons = Orange; break;
                case "white": priRing = priLabel = secRing = secLabel = play = RingDefault; icons = IconDefault; break;
                case "twotone": priRing = secRing = Orange; priLabel = secLabel = RingDefault; play = Orange; icons = IconDefault; break;
                case "inverse": priRing = secRing = RingDefault; priLabel = secLabel = Orange; play = Orange; icons = Orange; break;
                default: priRing = priLabel = RingDefault; secRing = secLabel = Orange; play = Orange; icons = IconDefault; break;
            }

            SkipBackRing.Stroke = priRing; SkipForwardRing.Stroke = priRing;
            SkipBackArrow.Fill = SkipBackArrow.Stroke = priRing;
            SkipForwardArrow.Fill = SkipForwardArrow.Stroke = priRing;
            SkipBackLabel.Foreground = priLabel; SkipForwardLabel.Foreground = priLabel;
            SecondarySkipBackIcon.Stroke = secRing; SecondarySkipForwardIcon.Stroke = secRing;
            SecondarySkipBackHead.Fill = SecondarySkipBackHead.Stroke = secRing;
            SecondarySkipForwardHead.Fill = SecondarySkipForwardHead.Stroke = secRing;
            SecondarySkipBackLabel.Foreground = secLabel; SecondarySkipForwardLabel.Foreground = secLabel;
            ApplySkipLabels(settings);
            SecondarySkipBackButton.ToolTip = $"Back {settings.SecondarySkipSeconds} s";
            SecondarySkipForwardButton.ToolTip = $"Forward {settings.SecondarySkipSeconds} s";

            // Outline (render 02): ring r 10.4/24, 1.25 stroke, 10 % fill, coloured glyph.
            // Solid: a filled disc with a dark glyph.
            var hollow = settings.IconStyle == "hollow";
            var playColor = ((SolidColorBrush)play).Color;
            PlayRing.Stroke = hollow ? play : Brushes.Transparent;
            PlayRing.Fill = hollow ? (playColor == Brand.Orange ? OrangeTint : WhiteTint) : play;
            PlayGlyph.Fill = PlayGlyph.Stroke = hollow ? play : Dark;
            foreach (var child in PauseGlyph.Children)
                if (child is System.Windows.Shapes.Rectangle r) r.Fill = hollow ? play : Dark;

            VolumeButton.Foreground = icons; ApplyGlyphBrush(VolumeButton, icons);
            FullscreenButton.Foreground = icons; ApplyGlyphBrush(FullscreenButton, icons);
            // The player view re-colours these three from their own state right after
            // (on, a non-1x speed, a running timer); the preview shows them at rest.
            CaptionsButton.Foreground = icons; ApplyGlyphBrush(CaptionsButton, icons);
            SpeedButton.Foreground = icons; ApplyGlyphBrush(SpeedButton, icons);
            SleepButton.Foreground = icons; ApplyGlyphBrush(SleepButton, icons);

            SpeedButton.Visibility = settings.ShowSpeed ? Visibility.Visible : Visibility.Collapsed;
            SleepButton.Visibility = settings.ShowSleepTimer ? Visibility.Visible : Visibility.Collapsed;
            return icons;
        }

        /// <summary>What the bar draws, for tests and the capture build's readback.</summary>
        internal System.Collections.Generic.Dictionary<string, object?> StyleState() => new()
        {
            ["primary_ring"] = (SkipBackRing.Stroke as SolidColorBrush)?.Color.ToString(),
            ["primary_label"] = (SkipBackLabel.Foreground as SolidColorBrush)?.Color.ToString(),
            ["secondary_ring"] = (SecondarySkipBackIcon.Stroke as SolidColorBrush)?.Color.ToString(),
            ["play_fill"] = (PlayRing.Fill as SolidColorBrush)?.Color.ToString(),
            ["primary_text"] = SkipBackLabel.Text,
            ["secondary_text"] = SecondarySkipBackLabel.Text,
            ["speed_visible"] = SpeedButton.Visibility == Visibility.Visible,
            ["sleep_visible"] = SleepButton.Visibility == Visibility.Visible,
        };
    }
}
