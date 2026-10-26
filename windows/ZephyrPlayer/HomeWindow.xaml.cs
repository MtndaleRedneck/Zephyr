using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace ZephyrPlayer
{
    /// <summary>
    /// Home: the app's resting screen. When nothing
    /// is playing the app shows the designated movies folder (chosen in Settings).
    /// Folders navigate in place; double-clicking a video plays it on the player
    /// window. The phone can also push playback over /play while this is open.
    /// </summary>
    public partial class HomeWindow : Window
    {
        private readonly PlayerSettings _settings;
        private readonly PairedStore _store;
        private readonly Action<string> _playLocal;
        private readonly Func<string> _newPairCode;
        private string _currentRel = "";

        private sealed record Entry(string Name, bool Dir);

        public HomeWindow(PlayerSettings settings, PairedStore store, Action<string> playLocal, Func<string> newPairCode)
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            _settings = settings;
            _store = store;
            _playLocal = playLocal;
            _newPairCode = newPairCode;
            _settings.Changed += (_, __) => Dispatcher.InvokeAsync(Refresh);
            Activated += (_, __) => Refresh();
            Refresh();
        }

        public void ShowPairBanner(string code)
        {
            PairCodeText.Text = string.Join(" ", code.ToCharArray());
            PairBanner.Visibility = Visibility.Visible;
        }

        public void HidePairBanner() => PairBanner.Visibility = Visibility.Collapsed;

        public void Refresh()
        {
            var folder = _settings.LibraryFolder;
            var hasFolder = folder.Length > 0 && Directory.Exists(folder);
            EmptyState.Visibility = hasFolder ? Visibility.Collapsed : Visibility.Visible;
            LibraryView.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
            if (hasFolder)
            {
                FolderPathText.Text = _currentRel.Length == 0 ? folder : Path.Combine(folder, _currentRel);
                LoadEntries(folder, _currentRel);
            }
            var paired = _store.Count;
            StatusText.Text = paired > 0
                ? $"Paired with {paired} phone{(paired == 1 ? "" : "s")} - pick a movie here or from your phone."
                : "Not paired - open Settings to pair your phone, or play a local file.";
        }

        private void LoadEntries(string root, string rel)
        {
            MovieList.Items.Clear();
            var (status, json) = Library.List(root, rel);
            if (status != 200)
            {
                FolderPathText.Text = root + "  (" + status + ")";
                return;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var name = e.GetProperty("name").GetString() ?? "";
                var dir = e.GetProperty("dir").GetBoolean();
                MovieList.Items.Add(new Entry((dir ? "📁 " : "🎬 ") + name, dir));
            }
            UpButton.IsEnabled = rel.Length > 0;
        }

        private void OpenSelected()
        {
            if (MovieList.SelectedItem is not Entry entry) return;
            var name = entry.Name.Length > 2 ? entry.Name.Substring(2) : entry.Name;
            var rel = _currentRel.Length == 0 ? name : _currentRel + "/" + name;
            if (entry.Dir)
            {
                _currentRel = rel;
                Refresh();
            }
            else
            {
                var full = Path.Combine(_settings.LibraryFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                _playLocal(new Uri(full).AbsoluteUri);
            }
        }

        private void OnEntryOpen(object sender, MouseButtonEventArgs e) => OpenSelected();

        private void OnEntryKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OpenSelected();
        }

        private void OnUp(object sender, RoutedEventArgs e)
        {
            var idx = _currentRel.LastIndexOf('/');
            _currentRel = idx < 0 ? "" : _currentRel.Substring(0, idx);
            Refresh();
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_settings, _store, _newPairCode) { Owner = this };
            w.ShowDialog();
            Refresh();
        }
    }
}
