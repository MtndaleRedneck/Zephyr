using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZephyrPlayer
{
    /// <summary>
    /// Home: the app's resting screen, now an Explorer-style icon grid of the
    /// designated movies folder (owner-approved look): back/up + clickable
    /// breadcrumb + search box on a toolbar, large folder tiles with a poster
    /// peeking out of the wireframe folder outline, loose videos as film-strip
    /// frames around the real Windows shell thumbnail, names under each tile.
    /// Folders navigate in place; double-clicking a video plays it on the
    /// player window. The phone can also push playback over /play meanwhile.
    /// </summary>
    public partial class HomeWindow : Window
    {
        private readonly PlayerSettings _settings;
        private readonly PairedStore _store;
        private readonly Action<string> _playLocal;
        private readonly Action _openPairing;
        private string _currentRel = "";
        private readonly List<string> _backStack = new();
        private List<Entry> _all = new();
        private int _thumbGeneration;

        private sealed record Crumb(string Label, string Rel);

        private sealed class Entry : INotifyPropertyChanged
        {
            public required string Name { get; init; }
            public required bool Dir { get; init; }
            public required string Rel { get; init; }
            public Visibility FolderVis => Dir ? Visibility.Visible : Visibility.Collapsed;
            public Visibility FileVis => Dir ? Visibility.Collapsed : Visibility.Visible;

            private ImageSource? _peek;
            public ImageSource? Peek { get => _peek; set { _peek = value; PropertyChanged?.Invoke(this, new(nameof(Peek))); } }
            private ImageSource? _thumb;
            public ImageSource? Thumb { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); } }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public HomeWindow(PlayerSettings settings, PairedStore store, Action<string> playLocal, Action openPairing)
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            _settings = settings;
            _store = store;
            _playLocal = playLocal;
            _openPairing = openPairing;
            _settings.Changed += (_, __) => Dispatcher.InvokeAsync(OnSettingsChanged);
            Activated += (_, __) => RefreshStatus();
            ConnectionState.Changed += () => Dispatcher.InvokeAsync(RefreshStatus);
            Refresh();
        }

        private string _lastFolder = "";
        private SettingsWindow? _settingsWin;

        /// <summary>Bar/volume/skip changes live-apply elsewhere and must not rebuild the
        /// library view. Only a library-folder change reloads - and reloads to the NEW
        /// folder's root (review should-fix 7).</summary>
        private void OnSettingsChanged()
        {
            var folder = _settings.LibraryFolder;
            if (folder == _lastFolder) return;
            _currentRel = "";
            _backStack.Clear();
            SearchBox.Text = "";
            Refresh();
        }

        public void Refresh()
        {
            var folder = _settings.LibraryFolder;
            _lastFolder = folder;
            var hasFolder = folder.Length > 0 && Directory.Exists(folder);
            EmptyState.Visibility = hasFolder ? Visibility.Collapsed : Visibility.Visible;
            LibraryView.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
            if (hasFolder)
            {
                BuildBreadcrumb(folder);
                LoadEntries(folder, _currentRel);
            }
            RefreshStatus();
        }

        public void RefreshStatus()
        {
            var paired = _store.Count;
            var status = paired > 0
                ? $"Paired with {paired} phone{(paired == 1 ? "" : "s")} - pick a movie here or from your phone."
                : "Not paired - open pairing to connect your phone, or play a local file.";
            // Connection findings surface here too (they live on the Pairing screen).
            if (ConnectionState.DiscoveryOff)
                status += "  ·  Find my PC is off: another app is using it.";
            else if (ConnectionState.PublicWifiName != null)
                status += "  ·  Windows is treating this Wi-Fi as public, which can block your phone.";
            StatusText.Text = status;
        }

        // ---------- breadcrumb ----------

        private void BuildBreadcrumb(string folder)
        {
            var crumbs = new List<Crumb>
            {
                new(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder, "")
            };
            var rel = "";
            foreach (var part in _currentRel.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                rel = rel.Length == 0 ? part : rel + "/" + part;
                crumbs.Add(new Crumb(part, rel));
            }
            Breadcrumb.Items.Clear();
            for (var i = 0; i < crumbs.Count; i++)
            {
                if (i > 0) Breadcrumb.Items.Add(new System.Windows.Controls.TextBlock
                {
                    Text = ">", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0),
                });
                var crumb = crumbs[i];
                var b = new System.Windows.Controls.Button { Content = crumb.Label, Style = (Style)FindResource("CrumbButton") };
                b.Click += (_, __) => NavigateTo(crumb.Rel, pushHistory: true);
                Breadcrumb.Items.Add(b);
            }
        }

        // ---------- entries ----------

        private void LoadEntries(string root, string rel)
        {
            var (status, json) = Library.List(root, rel);
            if (status != 200)
            {
                _all = new List<Entry>();
                MovieList.ItemsSource = _all;
                EmptyFolderText.Text = "Windows won't show that folder here.";
                EmptyFolderText.Visibility = Visibility.Visible;
                return;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var entries = new List<Entry>();
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var name = e.GetProperty("name").GetString() ?? "";
                var dir = e.GetProperty("dir").GetBoolean();
                var childRel = rel.Length == 0 ? name : rel + "/" + name;
                entries.Add(new Entry { Name = name, Dir = dir, Rel = childRel });
            }
            _all = entries;
            ApplySearch();
            BackButton.IsEnabled = _backStack.Count > 0;
            UpButton.IsEnabled = rel.Length > 0;
            LoadThumbnails(root, entries);
        }

        /// <summary>Real shell thumbnails, loaded off the UI thread; each tile
        /// fills in when its bitmap is ready. Folders peek the first video
        /// inside them (one level); files show their own frame.</summary>
        private void LoadThumbnails(string root, List<Entry> entries)
        {
            if (!OperatingSystem.IsWindows()) return; // headless CI keeps placeholders
            // CI evidence seam (never set in normal use): ZEPHYR_THUMB_FIXTURE_DIR points at a
            // folder of "<entry name>.png" bitmaps. Windows Server runners ship no video
            // thumbnail extractor (the shell call returns null - see the capture-notes probe),
            // so the populated-grid composition (poster peek + film-strip frame around REAL
            // bitmap pixels) cannot be pixel-proven through the shell there. With the seam,
            // real bitmaps travel the exact Peek/Thumb pipeline the shell bitmaps would take.
            // When unset, behavior is unchanged: shell thumbnails or drawn placeholders only.
            var fixtureDir = Environment.GetEnvironmentVariable("ZEPHYR_THUMB_FIXTURE_DIR");
            var generation = ++_thumbGeneration;
            Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    if (generation != _thumbGeneration) return; // navigated away
                    ImageSource? img = null;
                    try
                    {
                        if (fixtureDir != null)
                        {
                            var fx = Path.Combine(fixtureDir, entry.Name + ".png");
                            if (File.Exists(fx))
                            {
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.UriSource = new Uri(fx);
                                bmp.EndInit();
                                bmp.Freeze();
                                img = bmp;
                            }
                        }
                        if (img == null)
                        {
                            var full = Path.Combine(root, entry.Rel.Replace('/', Path.DirectorySeparatorChar));
                            if (entry.Dir)
                            {
                                var firstVideo = Directory.GetFiles(full)
                                    .Where(f => LibraryIsVideo(f))
                                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                    .FirstOrDefault();
                                if (firstVideo != null) img = ShellThumbs.Get(firstVideo, 192);
                            }
                            else
                            {
                                img = ShellThumbs.Get(full, 256);
                            }
                        }
                    }
                    catch { }
                    if (img == null) continue;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation != _thumbGeneration) return;
                        if (entry.Dir) entry.Peek = img; else entry.Thumb = img;
                    }));
                }
            });
        }

        private static bool LibraryIsVideo(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase) || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- search (filters the current folder's tiles, Explorer-style box) ----------

        private void ApplySearch()
        {
            var q = SearchBox.Text.Trim();
            MovieList.ItemsSource = q.Length == 0
                ? _all
                : _all.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            EmptyFolderText.Text = q.Length == 0 ? "This folder is empty." : "Nothing here matches \"" + q + "\".";
            EmptyFolderText.Visibility = ((IEnumerable<Entry>)MovieList.ItemsSource).Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplySearch();
        }

        // ---------- navigation ----------

        private void NavigateTo(string rel, bool pushHistory)
        {
            if (pushHistory) _backStack.Add(_currentRel);
            _currentRel = rel;
            SearchBox.Text = "";
            Refresh();
        }

        private void OpenSelected()
        {
            if (MovieList.SelectedItem is not Entry entry) return;
            if (entry.Dir)
            {
                NavigateTo(entry.Rel, pushHistory: true);
            }
            else
            {
                var full = Path.Combine(_settings.LibraryFolder, entry.Rel.Replace('/', Path.DirectorySeparatorChar));
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
            NavigateTo(idx < 0 ? "" : _currentRel.Substring(0, idx), pushHistory: true);
        }

        private void OnBack(object sender, RoutedEventArgs e)
        {
            if (_backStack.Count == 0) return;
            var rel = _backStack[^1];
            _backStack.RemoveAt(_backStack.Count - 1);
            _currentRel = rel;
            SearchBox.Text = "";
            Refresh();
        }

        /// <summary>One Settings window at a time, non-modal: an open Settings window
        /// must not freeze playback controls (review should-fix 6).</summary>
        private void OnSettings(object sender, RoutedEventArgs e)
        {
            if (_settingsWin != null) { _settingsWin.Activate(); return; }
            _settingsWin = new SettingsWindow(_settings, _store, _openPairing) { Owner = this };
            _settingsWin.Closed += (_, __) => _settingsWin = null;
            _settingsWin.Show();
        }
    }
}
