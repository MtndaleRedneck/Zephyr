using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZephyrPlayer
{
    /// <summary>
    /// Compact centered settings window (tabbed
    /// layout): clickable section list on the LEFT, one section's content on
    /// the right, Feedback & bugs and Updates pinned at the bottom with the
    /// version under Updates. Every change writes through PlayerSettings,
    /// whose Changed event restyles the player live.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        internal static Func<PairingPanel>? PairingFactory;
        private PairingPanel? _pairPanel;
        private readonly PlayerSettings _settings;
        private readonly PairedStore _store;
        private readonly Action _openPairing;
        private (string Version, string ExeUrl, string? Sha256)? _updateOffer;
        private readonly System.Windows.Threading.DispatcherTimer _updateStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        public SettingsWindow(PlayerSettings settings, PairedStore store, Action openPairing)
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
            _settings = settings;
            _store = store;
            _openPairing = openPairing;
            _store.Changed += OnStoreChanged;
            UpdateStatusText.Text = UpdateState.Display(UpdateState.Outcome);
            _updateStatusTimer.Tick += (_, __) =>
            {
                if (!InstallUpdateButton.IsEnabled) return;
                var offer = UpdateState.AvailableOffer;
                _updateOffer = offer == null ? null : (offer.Version, offer.ExeUrl, offer.Sha256);
                InstallUpdateButton.Visibility = _updateOffer != null ? Visibility.Visible : Visibility.Collapsed;
                UpdateStatusText.Text = UpdateState.Display(UpdateState.Outcome);
            };
            NavVersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "?");
            Loaded += (_, __) => _updateStatusTimer.Start();
            Closed += (_, __) => { _updateStatusTimer.Stop(); _store.Changed -= OnStoreChanged; };
            if (PairingFactory != null) { _pairPanel = PairingFactory(); PairingHost.Content = _pairPanel; PairButton.Visibility = Visibility.Collapsed; PairStatusText.Visibility = Visibility.Collapsed; PairHelpText.Visibility = Visibility.Collapsed; }
            FeedbackHost.Content = new FeedbackPanel(_store.Count) { MinHeight = 420 };
            _settings.Changed += OnSettingsUpdated;
            Closed += (_, __) => _settings.Changed -= OnSettingsUpdated;
            WireSections();
            Refresh();
        }

        private readonly Dictionary<string, (Button Nav, UIElement Section, string Title)> _sections = new();

        private void WireSections()
        {
            _sections["movies"] = (NavMovies, SectionMovies, "Movies folder");
            _sections["volume"] = (NavVolume, SectionVolume, "Volume");
            _sections["skip"] = (NavSkip, SkipSettingsCard, "Skip buttons");
            _sections["bar"] = (NavBar, SectionBar, "Playback bar");
            _sections["remote"] = (NavRemote, SectionRemote, "Paired phones");
            _sections["feedback"] = (NavFeedback, SectionFeedback, "Feedback & bugs");
            _sections["updates"] = (NavUpdates, SectionUpdates, "Updates");
            SelectSection("volume");
        }

        private void OnNav(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string key) SelectSection(key);
        }

        public void SelectSection(string key)
        {
            foreach (var (k, (nav, section, _)) in _sections)
            {
                var selected = k == key;
                section.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                // The template tree does not exist until the control is applied;
                // force it so construction-time selection cannot null-ref.
                nav.ApplyTemplate();
                if (nav.Template.FindName("Pill", nav) is FrameworkElement pill)
                    pill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                if (nav.Template.FindName("Bg", nav) is Border bg)
                    bg.Background = selected ? (Brush)FindResource("NavSelectedBrush") : Brushes.Transparent;
            }
            SectionTitle.Text = _sections[key].Title;
        }

        private void OnSettingsUpdated(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdatePreview);
        private void UpdatePreview()
        {
            if (BarPreview.Content is PlaybackPreview preview) preview.Update(_settings);
            else BarPreview.Content = new PlaybackPreview(_settings);
        }

        private void OnStoreChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            FolderText.Text = _settings.LibraryFolder.Length > 0 ? _settings.LibraryFolder : "Not set";
            BuildChoices(BoostPanel, PlayerSettings.BoostChoices, v => v + "%", () => _settings.VolumeBoostMaxPct, v => _settings.VolumeBoostMaxPct = v, "boost");
            BuildChoices(PrimarySkipPanel, PlayerSettings.PrimarySkipChoices, SkipLabel, () => _settings.PrimarySkipSeconds, v => _settings.PrimarySkipSeconds = v, "primaryskip");
            BuildChoices(SecondarySkipPanel, PlayerSettings.SecondarySkipChoices, SkipLabel, () => _settings.SecondarySkipSeconds, v => _settings.SecondarySkipSeconds = v, "secondaryskip");
            BuildChoices(BarColorPanel, PlayerSettings.BarColorStyles, Label, () => _settings.BarColorStyle, v => _settings.BarColorStyle = v, "barcolor");
            BuildChoices(IconStylePanel, PlayerSettings.IconStyles, Label, () => _settings.IconStyle, v => _settings.IconStyle = v, "iconstyle");
            FlashToggle.IsChecked = _settings.ButtonFlash;
            SpeedToggle.IsChecked = _settings.ShowSpeed;
            SleepToggle.IsChecked = _settings.ShowSleepTimer;
            PairStatusText.Text = _store.Count > 0
                ? $"Paired with {_store.Count} phone{(_store.Count == 1 ? "" : "s")}."
                : "Not paired yet.";
            UpdatePreview();
            VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "?");
        }

        private static string SkipLabel(int seconds) => PlayerSettings.FormatSkip(seconds);

        private static string Label(string v) => v switch
        {
            "original" => "Original", "orange" => "Orange", "white" => "White", "twotone" => "White, orange rings", "inverse" => "Orange, white rings",
            "hollow" => "Outline", "filled" => "Solid", _ => v,
        };

        private void BuildChoices<T>(WrapPanel panel, T[] choices, Func<T, string> label, Func<T> get, Action<T> set, string group)
        {
            panel.Children.Clear();
            foreach (var c in choices)
            {
                var rb = new RadioButton { Content = label(c), GroupName = group, IsChecked = Equals(get(), c) };
                var value = c;
                rb.Click += (_, __) => set(value);
                panel.Children.Add(rb);
            }
        }

        private void OnFlashToggle(object sender, RoutedEventArgs e) => _settings.ButtonFlash = FlashToggle.IsChecked == true;

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            _settings.ShowSpeed = SpeedToggle.IsChecked == true;
            _settings.ShowSleepTimer = SleepToggle.IsChecked == true;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Pick your designated movies folder",
                InitialDirectory = _settings.LibraryFolder,
            };
            if (dlg.ShowDialog(this) == true)
            {
                _settings.LibraryFolder = dlg.FolderName;
                Refresh();
            }
        }

        private void OnPair(object sender, RoutedEventArgs e)
        {
            // One shared Pairing screen (the old button minted a code here that
            // silently killed the code on Home - teardown pairing finding).
            _openPairing();
        }

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

        private void OnFeedback(object sender, RoutedEventArgs e)
        {
            var w = new FeedbackWindow(_store.Count) { Owner = this };
            w.ShowDialog();
        }
    }
}
