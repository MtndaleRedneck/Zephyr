using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace ZephyrPlayer
{
    /// <summary>The 2.0.2 bar pop-ups in the 3.0 shell: Subtitles (tracks, Add subtitle file, Size, Delay),
    /// Playback speed (chips and slider), Sleep timer (pauses, End of this movie), and the one-line notice.
    /// One pop-up is open at a time, anchored above its own button.</summary>
    public partial class MainWindow
    {
        private static readonly SolidColorBrush PopupCardBrush = Frozen(Color.FromArgb(0xF0, 0x16, 0x16, 0x1A));
        private static readonly SolidColorBrush PopupLineBrush = Frozen(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush ChipBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x44));
        private static readonly SolidColorBrush ChipOnBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0x95, 0x00));
        private static readonly SolidColorBrush RowOnBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private Border _subtitlesPopup = null!, _speedPopup = null!, _sleepPopup = null!, _osd = null!;
        private StackPanel _subtitleTracks = null!, _sleepRows = null!;
        private UniformGrid _speedChips = null!;
        private Slider _speedSlider = null!;
        private TextBlock _speedHeader = null!, _sleepHeader = null!, _subDelayText = null!, _osdText = null!;
        private Button _subSizeS = null!, _subSizeM = null!, _subSizeL = null!;
        private Border? _openPopup;
        private Button? _openPopupButton;
        private bool _speedSync;
        private double _rate = 1.0;
        private int _subDelayMs;
        private readonly SleepTimerState _sleep = new();
        private readonly DispatcherTimer _sleepTick = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _restartNoticeOpen;

        /// <summary>The subtitle size LibVLC was created with (App sets it); a different saved size takes effect
        /// only after Quit from the tray and opening Zephyr Player again.</summary>
        internal string LibVlcSubtitleSize { get; set; } = "M";
        internal int SubtitleRestartNoticeCount { get; private set; }
        internal string? LastSubtitleRestartNotice { get; private set; }
        /// <summary>Test seam: when set, the restart notice goes here instead of a dialog.</summary>
        internal Action<string, string>? RestartNoticeOverride { get; set; }
        internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        internal SleepTimerState Sleep => _sleep;
        internal int SleepPausedCount { get; private set; }
        internal string? LastSleepFire { get; private set; }
        internal double CurrentRate => _rate;
        internal int SubtitleDelayMs => _subDelayMs;
        internal bool IsPopupOpen => _openPopup != null;
        internal string? OpenPopupName => _openPopup == null ? null : _openPopup == _subtitlesPopup ? "subtitles" : _openPopup == _speedPopup ? "speed" : "sleep";
        internal string? OsdMessage => _osd.Visibility == Visibility.Visible ? _osdText.Text : null;

        private static TextBlock Label(string text, double size = 13, bool bold = false, Brush? fg = null) => new()
        {
            Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = fg ?? (Brush)new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)), VerticalAlignment = VerticalAlignment.Center,
        };

        private static Border PopupCard(double width, string name, UIElement content) => new()
        {
            Width = width, Background = PopupCardBrush, BorderBrush = PopupLineBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 10), Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.5, Direction = 270 },
        }.Named(name);

        private Button Chip(string text, RoutedEventHandler click, double width = double.NaN, double height = 26)
        {
            var b = new Button { Content = text, Height = height, MinWidth = 36, Padding = new Thickness(10, 0, 10, 0), Cursor = Cursors.Hand,
                Background = ChipBrush, Foreground = (Brush)new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
            if (!double.IsNaN(width)) b.Width = width;
            b.Click += click;
            return b;
        }

        private static void SetChipOn(Button b, bool on)
        {
            b.Background = on ? ChipOnBrush : ChipBrush;
            b.Foreground = on ? Orange : new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
            b.BorderBrush = on ? Orange : Brushes.Transparent;
        }

        private Button Row(string text, bool selected, RoutedEventHandler click, Brush? fg = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Label(text, 13, false, fg));
            if (selected)
            {
                var check = new System.Windows.Shapes.Path { Data = Geometry.Parse("M2,7 L6,11 L13,3"), Stroke = Orange, StrokeThickness = 1.8, Width = 14, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 1); grid.Children.Add(check);
            }
            var b = new Button { Content = grid, Height = 32, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 0, 10, 0), Cursor = Cursors.Hand,
                Background = selected ? RowOnBrush : Brushes.Transparent, BorderThickness = new Thickness(0) };
            b.Click += click;
            return b;
        }

        private static Grid Header(string title, out TextBlock value)
        {
            var g = new Grid { Margin = new Thickness(6, 2, 6, 8) };
            g.Children.Add(Label(title, 13, true));
            value = Label("", 13, true, Orange);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(value);
            return g;
        }

        private static Border Sep() => new() { Height = 1, Background = PopupLineBrush, Margin = new Thickness(2, 6, 2, 6) };

        private void InitPopups()
        {
            // Subtitles
            var sub = new StackPanel();
            sub.Children.Add(Header("Subtitles", out _));
            _subtitleTracks = new StackPanel();
            sub.Children.Add(_subtitleTracks);
            sub.Children.Add(Row("+  Add subtitle file...", false, (_, __) => { ClosePopups(); AddSubtitleFile(); }, Orange).Named("AddSubtitleRow"));
            sub.Children.Add(Sep());
            var size = new Grid { Margin = new Thickness(6, 4, 6, 4) };
            size.Children.Add(Label("Size"));
            var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _subSizeS = Chip("S", OnSubtitleSize); _subSizeM = Chip("M", OnSubtitleSize); _subSizeL = Chip("L", OnSubtitleSize);
            _subSizeM.Margin = new Thickness(6, 0, 0, 0); _subSizeL.Margin = new Thickness(6, 0, 0, 0);
            _subSizeS.ToolTip = "Small - applies after you quit Zephyr Player and open it again";
            _subSizeM.ToolTip = "Medium - applies after you quit Zephyr Player and open it again";
            _subSizeL.ToolTip = "Large - applies after you quit Zephyr Player and open it again";
            chips.Children.Add(_subSizeS); chips.Children.Add(_subSizeM); chips.Children.Add(_subSizeL);
            size.Children.Add(chips);
            sub.Children.Add(size);
            var delay = new Grid { Margin = new Thickness(6, 8, 6, 2) };
            delay.Children.Add(Label("Delay"));
            var dp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var minus = Chip("-", (_, __) => SetSubtitleDelay(_subDelayMs - 100), 28); minus.ToolTip = "Subtitles 0.1 s earlier";
            var plus = Chip("+", (_, __) => SetSubtitleDelay(_subDelayMs + 100), 28); plus.ToolTip = "Subtitles 0.1 s later";
            System.Windows.Automation.AutomationProperties.SetName(minus, "Subtitle delay earlier");
            System.Windows.Automation.AutomationProperties.SetName(plus, "Subtitle delay later");
            minus.MinWidth = 28; plus.MinWidth = 28; minus.Padding = plus.Padding = new Thickness(0);
            _subDelayText = Label("+0.0 s"); _subDelayText.Width = 52; _subDelayText.TextAlignment = TextAlignment.Center; _subDelayText.Margin = new Thickness(6, 0, 6, 0);
            dp.Children.Add(minus); dp.Children.Add(_subDelayText); dp.Children.Add(plus);
            delay.Children.Add(dp);
            sub.Children.Add(delay);
            _subtitlesPopup = PopupCard(300, "SubtitlesPopup", sub);

            // Speed
            var sp = new StackPanel();
            sp.Children.Add(Header("Playback speed", out _speedHeader));
            _speedChips = new UniformGrid { Columns = 3, Margin = new Thickness(-4) };
            foreach (var r in PlayerBarLogic.SpeedChips)
            {
                var rate = r;
                var c = Chip(PlayerBarLogic.ChipLabel(rate), (_, __) => SetRate(rate), double.NaN, 30);
                c.Margin = new Thickness(4); c.Padding = new Thickness(0);
                _speedChips.Children.Add(c);
            }
            sp.Children.Add(_speedChips);
            sp.Children.Add(new Border { Height = 1, Background = PopupLineBrush, Margin = new Thickness(2, 14, 2, 12) });
            var sg = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.Children.Add(Label("0.25x", 12, false, new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x98))));
            _speedSlider = new Slider { Minimum = 0.25, Maximum = 4, Value = 1, SmallChange = 0.05, LargeChange = 0.25, Margin = new Thickness(10, 0, 10, 0), Foreground = Orange };
            if (TryFindResource("PlaybackSlider") is Style ps) _speedSlider.Style = ps;
            _speedSlider.ValueChanged += (_, e) => { if (!_speedSync) SetRate(e.NewValue); };
            Grid.SetColumn(_speedSlider, 1); sg.Children.Add(_speedSlider);
            var four = Label("4x", 12, false, new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x98))); Grid.SetColumn(four, 2); sg.Children.Add(four);
            sp.Children.Add(sg);
            _speedPopup = PopupCard(330, "SpeedPopup", sp);

            // Sleep
            var sl = new StackPanel();
            sl.Children.Add(Header("Sleep timer", out _sleepHeader));
            _sleepRows = new StackPanel();
            sl.Children.Add(_sleepRows);
            sl.Children.Add(Sep());
            sl.Children.Add(Label("Pauses the movie when time is up", 12, false, new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x98))));
            ((FrameworkElement)sl.Children[sl.Children.Count - 1]).Margin = new Thickness(6, 4, 6, 2);
            _sleepPopup = PopupCard(290, "SleepPopup", sl);

            // One-line notice
            _osdText = Label("", 13, true);
            _osd = new Border { Background = PopupCardBrush, BorderBrush = PopupLineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 14, 8),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 76, 0, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false, Child = _osdText };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_osd, "Osd");
            _osdTimer.Tick += (_, __) => { _osdTimer.Stop(); _osd.Visibility = Visibility.Collapsed; };

            Overlay.Children.Add(_osd);
            Overlay.Children.Add(_subtitlesPopup);
            Overlay.Children.Add(_speedPopup);
            Overlay.Children.Add(_sleepPopup);

            _sleepTick.Tick += (_, __) => SleepTick();
            _player.EndReached += (_, __) => Dispatcher.BeginInvoke(new Action(OnMediaFinished));
            PreviewMouseDown += OnPopupOutsideClick;
            RefreshSubtitleSizeUi();
            RefreshSpeedUi();
            RefreshSleepUi();
        }

        // ---------- open / close ----------

        private void TogglePopup(Border popup, Button anchor)
        {
            if (_openPopup == popup) { ClosePopups(); return; }
            ClosePopups();
            if (popup == _subtitlesPopup) RefreshSubtitleTracks();
            if (popup == _sleepPopup) RefreshSleepUi();
            if (popup == _speedPopup) RefreshSpeedUi();
            popup.Visibility = Visibility.Visible;
            popup.UpdateLayout();
            _openPopup = popup; _openPopupButton = anchor;
            _menuOpen = true;
            PositionPopup(popup, anchor);
            ShowBarOnce();
        }

        private void PositionPopup(Border popup, Button anchor)
        {
            try
            {
                var center = anchor.TransformToAncestor(Overlay).Transform(new Point(anchor.ActualWidth / 2, 0)).X;
                var barTop = ControlBar.TransformToAncestor(Overlay).Transform(new Point(0, 0)).Y;
                var left = Math.Max(10, Math.Min(center - popup.Width / 2, Math.Max(10, Overlay.ActualWidth - popup.Width - 10)));
                popup.Margin = new Thickness(left, 0, 0, Math.Max(10, Overlay.ActualHeight - barTop + 12));
            }
            catch { popup.Margin = new Thickness(0, 0, 40, 130); popup.HorizontalAlignment = HorizontalAlignment.Right; }
        }

        internal void ClosePopups()
        {
            if (_openPopup == null) return;
            _openPopup.Visibility = Visibility.Collapsed;
            _openPopup = null; _openPopupButton = null;
            _menuOpen = false;
            ShowBarOnce();
        }

        private void OnPopupOutsideClick(object sender, MouseButtonEventArgs e)
        {
            if (_openPopup == null) return;
            var src = e.OriginalSource as DependencyObject;
            for (var d = src; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d == _openPopup || d == _openPopupButton) return;
            ClosePopups();
        }

        private void OnSpeed(object sender, RoutedEventArgs e) => TogglePopup(_speedPopup, SpeedButton);
        private void OnSleep(object sender, RoutedEventArgs e) => TogglePopup(_sleepPopup, SleepButton);
        private void OnCaptions(object sender, RoutedEventArgs e) => TogglePopup(_subtitlesPopup, CaptionsButton);

        // ---------- notice ----------

        internal void ShowOsd(string text)
        {
            _osdText.Text = text; _osd.Visibility = Visibility.Visible;
            _osdTimer.Stop(); _osdTimer.Start();
        }

        // ---------- subtitles ----------

        private void RefreshSubtitleTracks()
        {
            _subtitleTracks.Children.Clear();
            var tracks = _player.SpuDescription;
            var current = _player.Spu;
            var real = RealTracks(tracks);
            _subtitleTracks.Children.Add(Row("Off", current == -1 || real.All(t => t.Id != current), (_, __) => { SelectSubtitle(-1, "Off"); ClosePopups(); }));
            for (var i = 0; i < real.Length; i++)
            {
                var id = real[i].Id;
                var name = string.IsNullOrWhiteSpace(real[i].Name) ? "Track " + (i + 1) : real[i].Name;
                _subtitleTracks.Children.Add(Row(name, current == id, (_, __) => { SelectSubtitle(id, name); ClosePopups(); }));
            }
            RefreshSubtitleSizeUi();
            _subDelayText.Text = PlayerBarLogic.FormatDelay(_subDelayMs);
        }

        private void AddSubtitleFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add subtitle file",
                Filter = "Subtitles (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _player.AddSlave(MediaSlaveType.Subtitle, new Uri(dialog.FileName).AbsoluteUri, true);
                ShowOsd("Subtitles: " + System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Subtitle file refused: " + ex.GetType().Name);
                ShowOsd("That subtitle file could not be used");
            }
        }

        internal void SetSubtitleDelay(int ms)
        {
            _subDelayMs = Math.Clamp(ms, -60000, 60000);
            _player.SetSpuDelay(_subDelayMs * 1000L);
            _subDelayText.Text = PlayerBarLogic.FormatDelay(_subDelayMs);
        }

        private void OnSubtitleSize(object sender, RoutedEventArgs e) => ChooseSubtitleSize((string)((Button)sender).Content);

        internal void ChooseSubtitleSize(string size)
        {
            var before = _settings.SubtitleSize;
            _settings.SubtitleSize = size;      // saved now; App applies it when LibVLC is created at the next full start
            RefreshSubtitleSizeUi();
            var name = PlayerBarLogic.SubtitleSizeName(size);
            // Touches nothing that plays: no media, MediaPlayer, LibVLC or seek.
            if (size == LibVlcSubtitleSize) { ShowOsd("Subtitle size: " + name + " (the size in use now)"); return; }
            if (size == before) { ShowOsd("Subtitle size " + name + " is saved. It takes effect after you quit Zephyr Player from the tray icon and open it again."); return; }
            ShowSubtitleRestartNotice(size);
        }

        private void ShowSubtitleRestartNotice(string size)
        {
            var text = PlayerBarLogic.SubtitleRestartNotice(size);
            SubtitleRestartNoticeCount++;
            LastSubtitleRestartNotice = text;
            ClosePopups();
            if (RestartNoticeOverride != null) { RestartNoticeOverride(PlayerBarLogic.SubtitleRestartTitle, text); return; }
            _restartNoticeOpen = true;
            try { System.Windows.MessageBox.Show(this, text, PlayerBarLogic.SubtitleRestartTitle, MessageBoxButton.OK, MessageBoxImage.Information); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Restart notice failed: " + ex.GetType().Name); ShowOsd("Subtitle size saved. It takes effect after you quit Zephyr Player from the tray icon and open it again."); }
            finally { _restartNoticeOpen = false; }
        }

        private void RefreshSubtitleSizeUi()
        {
            if (_subSizeM == null || _settings is null) return;
            var size = _settings.SubtitleSize;
            SetChipOn(_subSizeS, size == "S"); SetChipOn(_subSizeM, size == "M"); SetChipOn(_subSizeL, size == "L");
        }

        // ---------- speed ----------

        internal void SetRate(double rate)
        {
            _rate = PlayerBarLogic.SnapRate(rate);
            _player.SetRate((float)_rate);
            RefreshSpeedUi();
        }

        private void RefreshSpeedUi()
        {
            if (_speedHeader == null) return;
            var normal = PlayerBarLogic.IsNormalRate(_rate);
            var label = PlayerBarLogic.FormatRate(_rate);
            _speedHeader.Text = label;
            var chip = PlayerBarLogic.ChipIndex(_rate);
            for (var i = 0; i < _speedChips.Children.Count; i++)
                if (_speedChips.Children[i] is Button b) SetChipOn(b, i == chip);
            _speedSync = true; _speedSlider.Value = _rate; _speedSync = false;
            SpeedLabel.Text = normal ? "" : label;
            SpeedLabel.Visibility = normal ? Visibility.Collapsed : Visibility.Visible;
            var brush = normal ? IconBrushNow() : Orange;
            SpeedButton.Foreground = brush; ApplyGlyphBrush(SpeedButton, brush);
            SpeedButton.ToolTip = normal ? "Playback speed" : "Playback speed (" + label + ")";
            Reflow();
        }

        // ---------- sleep timer ----------

        private Brush IconBrushNow() => _settings.BarColorStyle is "orange" or "inverse" ? Orange : RingDefault;

        internal void SetSleep(int minutes)
        {
            if (minutes == 0) _sleep.Off(); else _sleep.Set(minutes, Clock());
            _sleepEndGeneration = minutes == SleepTimerState.EndOfMovie ? _playGeneration : -1;
            if (_sleep.IsActive && !_sleep.IsEndOfMovie) _sleepTick.Start(); else _sleepTick.Stop();
            RefreshSleepUi();
        }

        internal void SleepTick()
        {
            if (_sleep.IsDue(Clock())) { SleepFire("timer"); return; }
            RefreshSleepUi();
        }

        private int _sleepEndGeneration = -1;

        /// <summary>"End of this movie" belongs to the start it was chosen on; a new start drops it.</summary>
        private void DropEndOfMovieSleep()
        {
            if (!_sleep.IsEndOfMovie) return;
            _sleep.Off(); _sleepEndGeneration = -1;
            EndOfMovieDroppedCount++;
            RefreshSleepUi();
        }

        /// <summary>Back/tray close: popups closed, timers stopped (the window itself is hidden by the App).</summary>
        public void HideOverlayWindow() { ClosePopups(); _idleTimer.Stop(); _clock.Stop(); }
        public void ReleasePlayback() { _idleTimer.Stop(); _clock.Stop(); _sleepTick.Stop(); StopPlaybackSource(); }

        /// <summary>Test seam: the code-built elements (pop-ups, rows, error card) by their stable names; other names go to FindName.</summary>
        internal object? NamedForTest(string name) => name switch
        {
            "SpeedPopup" => _speedPopup, "SleepPopup" => _sleepPopup, "SubtitlesPopup" => _subtitlesPopup,
            "SleepRows" => _sleepRows, "SpeedChips" => _speedChips, "SpeedSlider" => _speedSlider,
            "ErrorTitle" => _errorTitle, "ErrorMessage" => _errorMessage, "ErrorRetryButton" => _errorRetry, "ErrorBackButton" => _errorBack,
            _ => FindName(name)
        };

        internal float RateAtHandoff { get; private set; } = 1f;
        internal int RateReapplyCount { get; private set; }
        internal int EndOfMovieDroppedCount { get; private set; }
        internal int MediaEndedCount { get; private set; }
        internal bool SleepTickRunning => _sleepTick.IsEnabled;
        internal bool IsOpening => _opening;
        internal object? CurrentMedia => _safeSource?.Media;
        internal double LastSeekFraction { get; private set; } = -1;
        internal int PlayPauseHandedCount { get; private set; }
        /// <summary>Test seam: a key as the window's own PreviewKeyDown handler gets it.</summary>
        internal void HandleKey(KeyEventArgs e) => OnPlayerKeyDown(this, e);

        /// <summary>Time is up: PAUSE and stay in the player (progress saved). Never stop, never leave for Home.</summary>
        private void SleepFire(string why)
        {
            _sleepTick.Stop();
            if (_player.State is VLCState.Playing or VLCState.Buffering or VLCState.Opening) _player.SetPause(true);
            SavePlaybackProgress();
            _sleep.Off(); _sleepEndGeneration = -1;
            SleepPausedCount++; LastSleepFire = why;
            RefreshSleepUi();
            ShowBarOnce();
            _idleTimer.Stop();
            ShowOsd(why == "end" ? "Sleep timer: the movie has ended" : "Sleep timer: paused");
        }

        private void OnMediaFinished()
        {
            if (_nativeStart != null && _nativeStart.Waiting) return;      // EndReached before Playing is a failed start (FailNativeStart)
            MediaEndedCount++;
            ShowBarOnce();
            if (_sleep.IsEndOfMovie && _sleepEndGeneration == _playGeneration) SleepFire("end");
            UpdatePlayPauseGlyph();
        }

        private void RefreshSleepUi()
        {
            if (_sleepRows == null) return;
            var now = Clock();
            var on = _sleep.IsActive;
            SleepLabel.Text = _sleep.Badge(now);
            SleepLabel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Reflow();
            _sleepHeader.Text = _sleep.Header(now);
            _sleepRows.Children.Clear();
            foreach (var (label, minutes) in SleepTimerState.Choices)
            {
                var m = minutes;
                _sleepRows.Children.Add(Row(label, m == _sleep.Minutes, (_, __) => { SetSleep(m); ClosePopups(); }));
            }
            var brush = on ? Orange : IconBrushNow();
            SleepButton.Foreground = brush; ApplyGlyphBrush(SleepButton, brush);
            SleepButton.ToolTip = on ? "Sleep timer (" + _sleep.Header(now) + ")" : "Sleep timer";
        }
    }
}

namespace ZephyrPlayer
{
    internal static class PopupExtensions
    {
        internal static T Named<T>(this T element, string name) where T : System.Windows.FrameworkElement
        {
            System.Windows.Automation.AutomationProperties.SetAutomationId(element, name);
            return element;
        }
    }
}
