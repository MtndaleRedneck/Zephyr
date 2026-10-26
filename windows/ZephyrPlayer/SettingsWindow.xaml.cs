using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ZephyrPlayer
{
    /// <summary>
    /// Centered settings panel (an open box in the middle of the
    /// screen, live preview, not VLC's top-bar dialog). Every change writes
    /// through PlayerSettings, whose Changed event restyles the player live.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly PlayerSettings _settings;
        private readonly PairedStore _store;
        private readonly Func<string> _newPairCode;
        private (string Version, string ExeUrl, string? Sha256)? _updateOffer;
        private readonly System.Windows.Threading.DispatcherTimer _updateStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        public SettingsWindow(PlayerSettings settings, PairedStore store, Func<string> newPairCode)
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            _settings = settings;
            _store = store;
            _newPairCode = newPairCode;
            VersionText.Text = "Zephyr Player " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
            UpdateStatusText.Text = UpdateState.Display(UpdateState.Outcome);
            _updateStatusTimer.Tick += (_, __) => { if (InstallUpdateButton.IsEnabled && _updateOffer == null) UpdateStatusText.Text = UpdateState.Display(UpdateState.Outcome); };
            Loaded += (_, __) => _updateStatusTimer.Start();
            Closed += (_, __) => _updateStatusTimer.Stop();
            Refresh();
        }

        private void Refresh()
        {
            FolderText.Text = _settings.LibraryFolder.Length > 0 ? _settings.LibraryFolder : "Not set";
            BuildChoices(BoostPanel, PlayerSettings.BoostChoices, v => v + "%", () => _settings.VolumeBoostMaxPct, v => _settings.VolumeBoostMaxPct = v, "boost");
            BuildChoices(SkipBackPanel, PlayerSettings.SkipChoices, v => v + "s", () => _settings.SkipBackSeconds, v => _settings.SkipBackSeconds = v, "skipback");
            BuildChoices(SkipForwardPanel, PlayerSettings.SkipChoices, v => v + "s", () => _settings.SkipForwardSeconds, v => _settings.SkipForwardSeconds = v, "skipfwd");
            BuildChoices(BarColorPanel, PlayerSettings.BarColorStyles, Label, () => _settings.BarColorStyle, v => _settings.BarColorStyle = v, "barcolor");
            BuildChoices(IconStylePanel, PlayerSettings.IconStyles, Label, () => _settings.IconStyle, v => _settings.IconStyle = v, "iconstyle");
            SpeedToggle.IsChecked = _settings.ShowSpeed;
            SleepToggle.IsChecked = _settings.ShowSleepTimer;
            PairStatusText.Text = _store.Count > 0
                ? $"Paired with {_store.Count} phone{(_store.Count == 1 ? "" : "s")}."
                : "Not paired yet.";
        }

        private static string Label(string v) => v switch
        {
            "original" => "Original", "orange" => "Orange", "white" => "White", "twotone" => "Two-tone",
            "hollow" => "Hollow", "filled" => "Filled", _ => v,
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

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            _settings.ShowSpeed = SpeedToggle.IsChecked == true;
            _settings.ShowSleepTimer = SleepToggle.IsChecked == true;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Pick your designated movies folder",
                UseDescriptionForTitle = true,
                SelectedPath = _settings.LibraryFolder.Length > 0 ? _settings.LibraryFolder : "",
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _settings.LibraryFolder = dlg.SelectedPath;
                Refresh();
            }
        }

        private void OnPair(object sender, RoutedEventArgs e)
        {
            var code = _newPairCode();
            PairCodeText.Text = string.Join(" ", code.ToCharArray());
        }

        private void OnCheckUpdates(object sender, RoutedEventArgs e)
        {
            UpdateStatusText.Text = "Checking...";
            var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            Task.Run(() =>
            {
                string result;
                try
                {
                    UpdateState.Outcome = UpdateCheck.CheckOutcomeOrThrow(current);
                    _updateOffer = UpdateCheck.LatestNewerThanOrThrow(current);
                    result = UpdateState.Display(UpdateState.Outcome);
                }
                catch (Exception ex)
                {
                    UpdateState.Outcome = "failed: " + ex.GetType().Name;
                    result = UpdateState.Display(UpdateState.Outcome);
                }
                Dispatcher.InvokeAsync(() =>
                {
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
            var w = new FeedbackWindow { Owner = this };
            w.ShowDialog();
        }
    }
}
