using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZephyrPlayer
{
    /// <summary>
    /// The Settings window (CP3, renders 05, 06, 06b): a separate compact 900x600 window
    /// owned by the shell, centred on it, the shell dimmed behind it. Left nav (Volume,
    /// Skip buttons, Playback bar, Paired phones; Feedback &amp; bugs and Updates pinned at
    /// the bottom with the real version under them), one section at a time on the right.
    /// Every change writes through PlayerSettings, whose Changed event restyles the
    /// player AND the live preview (the real PlayerBar in preview mode).
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>Builds the one live pairing panel (App sets it: the panel needs the gate).</summary>
        internal static Func<PairingPanel>? PairingFactory;
        private readonly PlayerSettings _settings;
        private readonly PairedStore _store;
        private PairingPanel? _pairPanel;
        private FeedbackPanel? _feedbackPanel;
        private readonly PlayerBar _skipPreview = new();
        private readonly PlayerBar _barPreview = new();
        private (string Version, string ExeUrl, string? Sha256)? _updateOffer;
        private readonly System.Windows.Threading.DispatcherTimer _updateStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly Dictionary<string, (Button Nav, UIElement Section, string Title, string Description)> _sections = new();

        /// <summary>The tab on screen ("volume", "skip", "bar", "remote", "feedback", "updates").</summary>
        public string CurrentSection { get; private set; } = "volume";

        /// <summary>"Zephyr Player 1.4.0": the real assembly version (item 72).</summary>
        internal static string VersionLine => "Zephyr Player " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");

        public SettingsWindow(PlayerSettings settings, PairedStore store)
        {
            InitializeComponent();
            _settings = settings;
            _store = store;
            ZephyrPlayer.WindowChrome.ApplyDarkTitleBar(this);
            SourceInitialized += (_, __) => RoundCorners();
            CaptionIcon.Source = ShellWindow.IconFrame(36);
            NavVersionText.Text = VersionLine;
            VersionText.Text = VersionLine;

            _sections["volume"] = (NavVolume, SectionVolume, "Volume", "Zephyr's own volume, separate from Windows");
            _sections["skip"] = (NavSkip, SectionSkip, "Skip buttons", "Back and forward in each pair always use the same time.");
            _sections["bar"] = (NavBar, SectionBar, "Playback bar", "Changes show in the preview right away.");
            _sections["remote"] = (NavRemote, SectionRemote, "Paired phones", "");
            _sections["feedback"] = (NavFeedback, SectionFeedback, "Feedback & bugs", "Write what happened. You see exactly what gets sent before anything leaves this PC.");
            _sections["updates"] = (NavUpdates, SectionUpdates, "Updates", "Zephyr also checks automatically every time it opens.");

            // LIVE PREVIEW (item 73): the real bar in preview mode, one per tab.
            SkipPreviewBox.Child = PreviewFrame(_skipPreview);
            BarPreviewBox.Child = PreviewFrame(_barPreview);

            if (PairingFactory != null) { _pairPanel = PairingFactory(); SectionRemote.Content = _pairPanel; }
            _feedbackPanel = new FeedbackPanel(_store.Count);
            SectionFeedback.Content = _feedbackPanel;

            UpdateStatusText.Text = UpdateState.Display(UpdateState.Outcome);
            _updateStatusTimer.Tick += (_, __) =>
            {
                if (!InstallUpdateButton.IsEnabled) return;
                var offer = UpdateState.AvailableOffer;
                _updateOffer = offer == null ? null : (offer.Version, offer.ExeUrl, offer.Sha256);
                InstallUpdateButton.Visibility = _updateOffer != null ? Visibility.Visible : Visibility.Collapsed;
                UpdateStatusText.Text = UpdateState.Display(UpdateState.Outcome);
            };
            Loaded += (_, __) => _updateStatusTimer.Start();
            _store.Changed += OnStoreChanged;
            _settings.Changed += OnSettingsUpdated;
            Closed += (_, __) =>
            {
                _updateStatusTimer.Stop();
                _store.Changed -= OnStoreChanged;
                _settings.Changed -= OnSettingsUpdated;
            };
            Refresh();
            SelectSection("volume");
        }

        /// <summary>The bar is laid out at the player's windowed width and scaled down
        /// into the 118 px preview strip (render 06 / 06b).</summary>
        private FrameworkElement PreviewFrame(PlayerBar bar)
        {
            bar.ShowAsPreview(_settings);
            return new Grid { Width = 940, Children = { bar } };
        }

        // Windows 11 draws the radius-12 look itself (DWMWA_WINDOW_CORNER_PREFERENCE =
        // DWMWCP_ROUND); older Windows ignores the call and keeps square corners.
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private void RoundCorners()
        {
            try { int round = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int)); } catch { }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnNav(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.CommandParameter is string key) SelectSection(key);
        }

        public void SelectSection(string key)
        {
            if (!_sections.ContainsKey(key)) key = "volume";
            foreach (var (k, (nav, section, _, _)) in _sections)
            {
                var selected = k == key;
                section.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                nav.Tag = selected ? "sel" : "";
            }
            var s = _sections[key];
            SectionTitle.Text = s.Title;
            SectionDescription.Text = s.Description;
            SectionDescription.Visibility = s.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SectionScroll.ScrollToTop();
            CurrentSection = key;
        }

        private void OnSettingsUpdated(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Refresh);
        private void OnStoreChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

        /// <summary>Choices, toggles and both previews from the settings file.</summary>
        private void Refresh()
        {
            BuildChoices(BoostPanel, PlayerSettings.BoostChoices, v => v + "%", () => _settings.VolumeBoostMaxPct, v => _settings.VolumeBoostMaxPct = v, "boost", "ZChipRadio");
            BuildChoices(PrimarySkipPanel, PlayerSettings.PrimarySkipChoices, PlayerSettings.FormatSkip, () => _settings.PrimarySkipSeconds, v => _settings.PrimarySkipSeconds = v, "primaryskip", "ZChipRadio");
            BuildChoices(SecondarySkipPanel, PlayerSettings.SecondarySkipChoices, PlayerSettings.FormatSkip, () => _settings.SecondarySkipSeconds, v => _settings.SecondarySkipSeconds = v, "secondaryskip", "ZChipRadio");
            BuildChoices(IconStylePanel, PlayerSettings.IconStyles, Label, () => _settings.IconStyle, v => _settings.IconStyle = v, "iconstyle", "ZChipRadioSmall");
            BuildChoices(BarColorPanel, PlayerSettings.BarColorStyles, Label, () => _settings.BarColorStyle, v => _settings.BarColorStyle = v, "barcolor", "ZChipRadioSmall");
            AnimateToggle.IsChecked = _settings.AnimateOnPress;
            FlashToggle.IsChecked = _settings.ButtonFlash;
            SpeedToggle.IsChecked = _settings.ShowSpeed;
            SleepToggle.IsChecked = _settings.ShowSleepTimer;
            _skipPreview.ApplyStyle(_settings);
            _barPreview.ApplyStyle(_settings);
        }

        /// <summary>Chip labels as render 06 / 06b.</summary>
        internal static string Label(string v) => v switch
        {
            "original" => "Original", "orange" => "Orange", "white" => "White", "twotone" => "White, orange rings", "inverse" => "Orange, white rings",
            "hollow" => "Outline", "filled" => "Solid", _ => v,
        };

        private void BuildChoices<T>(WrapPanel panel, T[] choices, Func<T, string> label, Func<T> get, Action<T> set, string group, string styleKey)
        {
            var style = (Style)FindResource(styleKey);
            panel.Children.Clear();
            foreach (var c in choices)
            {
                var text = label(c);
                var rb = new RadioButton { Content = text, GroupName = group, IsChecked = Equals(get(), c), Style = style };
                System.Windows.Automation.AutomationProperties.SetAutomationId(rb, group + ":" + c);
                System.Windows.Automation.AutomationProperties.SetName(rb, text);
                var value = c;
                rb.Click += (_, __) => set(value);
                panel.Children.Add(rb);
            }
        }

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, AnimateToggle)) _settings.AnimateOnPress = AnimateToggle.IsChecked == true;
            else if (ReferenceEquals(sender, FlashToggle)) _settings.ButtonFlash = FlashToggle.IsChecked == true;
            else if (ReferenceEquals(sender, SpeedToggle)) _settings.ShowSpeed = SpeedToggle.IsChecked == true;
            else if (ReferenceEquals(sender, SleepToggle)) _settings.ShowSleepTimer = SleepToggle.IsChecked == true;
        }

        /// <summary>Test / readback seam: what both previews draw right now.</summary>
        internal (Dictionary<string, object?> Skip, Dictionary<string, object?> Bar) PreviewState() => (_skipPreview.StyleState(), _barPreview.StyleState());
        internal PlayerBar SkipPreviewBar => _skipPreview;
        internal PlayerBar BarPreviewBar => _barPreview;
        internal PairingPanel? PairPanel => _pairPanel;

        private void OnCheckUpdates(object sender, RoutedEventArgs e)
        {
            _updateOffer = null;
            UpdateState.AvailableOffer = null;
            UpdateState.Outcome = "checking";
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "Checking...";
            var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            Task.Run(() =>
            {
                string result;
                try
                {
                    UpdateState.Outcome = UpdateCheck.CheckOutcomeOrThrow(current);
                    result = UpdateState.Display(UpdateState.Outcome);
                }
                catch (Exception ex)
                {
                    UpdateState.Outcome = "failed: " + ex.GetType().Name;
                    result = UpdateState.Display(UpdateState.Outcome);
                }
                Dispatcher.InvokeAsync(() =>
                {
                    var offer = UpdateState.AvailableOffer;
                    _updateOffer = offer == null ? null : (offer.Version, offer.ExeUrl, offer.Sha256);
                    UpdateStatusText.Text = result;
                    InstallUpdateButton.Visibility = _updateOffer != null ? Visibility.Visible : Visibility.Collapsed;
                });
            });
        }

        private async void OnInstallUpdate(object sender, RoutedEventArgs e)
        {
            var offer = _updateOffer;
            if (offer == null) return;
            if (offer.Value.Sha256 == null)
            {
                UpdateStatusText.Text = "The release has no verified checksum. Update was not installed.";
                return;
            }
            InstallUpdateButton.IsEnabled = false;
            try
            {
                UpdateStatusText.Text = "Downloading update...";
                var progress = new Progress<int>(n => UpdateStatusText.Text = $"Downloading update: {n}%");
                var path = await SelfUpdate.Download(offer.Value.Version, offer.Value.ExeUrl, offer.Value.Sha256, progress);
                UpdateStatusText.Text = "Starting the verified installer...";
                SelfUpdate.LaunchAndQuit(path);
            }
            catch (Exception)
            {
                UpdateStatusText.Text = "Could not install the update. Your current version is unchanged. Try again later.";
                InstallUpdateButton.IsEnabled = true;
            }
        }
    }
}
