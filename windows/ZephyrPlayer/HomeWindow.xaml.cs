using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// designated movies folder (pairing layout): back/up + clickable
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
        private readonly PlaybackHistory _history = new();
        private readonly bool _loadDurations;

        private sealed record Crumb(string Label, string Rel);

        private sealed class Entry : INotifyPropertyChanged
        {
            public required string Name { get; init; }
            public required bool Dir { get; init; }
            public required string Rel { get; init; }
            public DateTime Created { get; init; }
            public string Detail { get; init; } = "";
            public Visibility FolderVis => Dir ? Visibility.Visible : Visibility.Collapsed;
            public Visibility FileVis => Dir ? Visibility.Collapsed : Visibility.Visible;
            private long _durationMs;
            private long _positionMs;
            public string Duration => PlaybackHistory.DurationLabel(_durationMs);
            public Visibility DurationVis => !Dir && _durationMs > 0 ? Visibility.Visible : Visibility.Collapsed;
            public double Progress => _durationMs > 0 ? Math.Clamp(1.0 * _positionMs / _durationMs, 0, 1) : 0;
            public Visibility ProgressVis => !Dir && Progress > 0 && Progress < .98 ? Visibility.Visible : Visibility.Collapsed;
            public Visibility WatchedVis => Dir && Name.Equals(".Watched", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            public void SetMetadata(long position, long duration)
            { _positionMs = position; _durationMs = duration; PropertyChanged?.Invoke(this, new(null)); }

            private ImageSource? _peek;
            public ImageSource? Peek { get => _peek; set { _peek = value; PropertyChanged?.Invoke(this, new(nameof(Peek))); } }
            private ImageSource? _thumb;
            public ImageSource? Thumb { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); } }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public HomeWindow(PlayerSettings settings, PairedStore store, Action<string> playLocal, Action openPairing, bool loadDurations = true)
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            _loadDurations = loadDurations;
            _settings = settings;
            _store = store;
            _playLocal = playLocal;
            _openPairing = openPairing;
            _settings.Changed += (_, __) => Dispatcher.InvokeAsync(OnSettingsChanged);
            Activated += (_, __) => { RefreshStatus(); foreach (var entry in _all.Where(e => !e.Dir)) { var item = _history.Get(Path.Combine(_settings.LibraryFolder, entry.Rel.Replace('/', Path.DirectorySeparatorChar))); if (item != null) entry.SetMetadata(item.PositionMs, item.DurationMs); } };
            ConnectionState.Changed += () => Dispatcher.InvokeAsync(RefreshStatus);
            var statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            statusTimer.Tick += (_, __) => RefreshStatus();
            Loaded += (_, __) => statusTimer.Start();
            Closed += (_, __) => statusTimer.Stop();
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
            if (folder == _lastFolder) { ApplySearch(); return; }
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
            var active = _store.Entries().FirstOrDefault(e => _store.IsConnected(e.Token));
            PhoneCardName.Text = active?.Name ?? (paired > 0 ? "Paired phones" : "Pair a phone");
            PhoneCardStatus.Text = active != null ? "Connected" : paired > 0 ? "Not connected" : "Not paired";
            PhoneConnectedDot.Visibility = active != null ? Visibility.Visible : Visibility.Collapsed;
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
                var path = Path.Combine(root, childRel.Replace('/', Path.DirectorySeparatorChar));
                DateTime created;
                try { created = File.GetCreationTimeUtc(path); } catch { created = DateTime.MinValue; }
                string detail;
                try { detail = dir ? Directory.EnumerateFiles(path).Count(LibraryIsVideo) + " videos" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " - " + FileSize(new FileInfo(path).Length); }
                catch { detail = dir ? "Folder" : "Video"; }
                var entry = new Entry { Name = name, Dir = dir, Rel = childRel, Created = created, Detail = detail };
                var saved = dir ? null : _history.Get(path);
                if (saved != null) entry.SetMetadata(saved.PositionMs, saved.DurationMs);
                entries.Add(entry);
            }
            CountText.Text = entries.Count(e => e.Dir) + " folders - " + entries.Count(e => !e.Dir) + " videos";
            _all = entries;
            ApplySearch();
            BackButton.IsEnabled = _backStack.Count > 0;
            UpButton.IsEnabled = rel.Length > 0;
            LoadThumbnails(root, entries);
            if (_loadDurations) LoadDurations(root, entries, _thumbGeneration);
        }

        /// <summary>First video inside a folder, descending into subfolders when the
        /// top level has none (series libraries). Bounded: at most 3 levels down and
        /// 40 VIDEO candidates examined, ordinal order. Non-video files (.srt, .nfo,
        /// posters) are skipped for free - a folder full of them must not spend the
        /// budget before the descent (round-3 P8). The per-folder OrderBy below
        /// materializes one directory's listing; the 40-folder budget still bounds the
        /// descent, so a huge directory never gets fully listed before the cap applies.</summary>
        private static string? PeekFirstVideo(string dir)
        {
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((dir, 0));
            var examined = 0;
            while (pending.Count > 0)
            {
                var (d, depth) = pending.Dequeue();
                IEnumerable<string> files, subs;
                try
                {
                    files = Directory.EnumerateFiles(d);
                    subs = Directory.EnumerateDirectories(d);
                }
                catch { continue; }
                foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!LibraryIsVideo(f)) continue;
                    if (++examined > 40) return null;
                    return f;
                }
                if (depth < 3)
                    foreach (var sub in subs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        if (++examined > 40) return null;
                        pending.Enqueue((sub, depth + 1));
                    }
            }
            return null;
        }

        /// <summary>Real shell thumbnails, loaded off the UI thread; each tile
        /// fills in when its bitmap is ready. Folders peek the first video
        /// inside them (up to 3 levels down); files show their own frame.</summary>
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
            // Test seam: COMPILED OUT of the shipped binary, same as every other
            // CI-only hook (round-6 S6-7); the capture build additionally opts in per
            // run with ZEPHYR_CI_CAPTURE.
#if CI_CAPTURE_HOOK
            var fixtureDir = Environment.GetEnvironmentVariable("ZEPHYR_CI_CAPTURE") != null
                ? Environment.GetEnvironmentVariable("ZEPHYR_THUMB_FIXTURE_DIR")
                : null;
#else
            string? fixtureDir = null;
#endif
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
                                var firstVideo = PeekFirstVideo(full);
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

        private void LoadDurations(string root, List<Entry> entries, int generation)
        {
            Task.Run(async () =>
            {
                try
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    using var vlc = new LibVLCSharp.Shared.LibVLC("--no-video", "--no-audio");
                    foreach (var entry in entries.Where(e => !e.Dir))
                    {
                        if (generation != _thumbGeneration) return;
                        var path = Path.Combine(root, entry.Rel.Replace('/', Path.DirectorySeparatorChar));
                        if (_history.Get(path) != null) continue;
                        try
                        {
                            using var media = new LibVLCSharp.Shared.Media(vlc, path, LibVLCSharp.Shared.FromType.FromPath);
                            await media.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseLocal, 1500);
                            var duration = media.Duration;
                            if (duration <= 0) continue;
                            _history.Save(path, 0, duration, metadataOnly: true);
                            Dispatcher.BeginInvoke(new Action(() => { if (generation == _thumbGeneration) entry.SetMetadata(_history.Get(path)?.PositionMs ?? 0, duration); }));
                        }
                        catch { }
                    }
                }
                catch { } // Missing codec/invalid file: omit the badge, never invent a duration.
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
            IEnumerable<Entry> filtered = _all.Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            var comparer = Comparer<string>.Create(NaturalCompare);
            IOrderedEnumerable<Entry> ordered = _settings.FoldersFirst
                ? filtered.OrderBy(e => !e.Dir)
                : filtered.OrderBy(e => 0);
            ordered = _settings.LibrarySort == "created"
                ? ordered.ThenByDescending(e => e.Created).ThenBy(e => e.Name, comparer)
                : ordered.ThenBy(e => e.Name, comparer);
            MovieList.ItemsSource = ordered.ToList();
            if (SortButton != null) SortButton.Content = _settings.LibrarySort == "created" ? "Sort: Date created" : "Sort: Name";
            EmptyFolderText.Text = q.Length == 0 ? "This folder is empty." : "Nothing here matches \"" + q + "\".";
            EmptyFolderText.Visibility = ((IEnumerable<Entry>)MovieList.ItemsSource).Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);
        private static int NaturalCompare(string? a, string? b) =>
            OperatingSystem.IsWindows() ? StrCmpLogicalW(a ?? "", b ?? "") : StringComparer.OrdinalIgnoreCase.Compare(a, b);

        private static string FileSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? (bytes / (1024d * 1024 * 1024)).ToString("0.#") + " GB" : bytes >= 1024 * 1024 ? (bytes / (1024d * 1024)).ToString("0.#") + " MB" : bytes >= 1024 ? (bytes / 1024d).ToString("0.#") + " KB" : bytes + " B";

        private void OnBrowseFolder(object sender, RoutedEventArgs e)
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose movies folder", InitialDirectory = _settings.LibraryFolder };
            if (picker.ShowDialog(this) == true) _settings.LibraryFolder = picker.FolderName;
        }
        private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();
        private void OnPairing(object sender, RoutedEventArgs e) => OpenSettings("remote");

        private void OnSort(object sender, RoutedEventArgs e)
        {
            var menu = new System.Windows.Controls.ContextMenu();
            void Add(string label, bool selected, Action act)
            {
                var item = new System.Windows.Controls.MenuItem { Header = label, IsCheckable = true, IsChecked = selected };
                item.Click += (_, __) => act();
                menu.Items.Add(item);
            }
            Add("Name", _settings.LibrarySort == "name", () => _settings.LibrarySort = "name");
            Add("Date created", _settings.LibrarySort == "created", () => _settings.LibrarySort = "created");
            menu.Items.Add(new System.Windows.Controls.Separator());
            Add("Folders first", _settings.FoldersFirst, () => _settings.FoldersFirst = true);
            Add("Mixed in", !_settings.FoldersFirst, () => _settings.FoldersFirst = false);
            menu.PlacementTarget = SortButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
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
            OpenSettings("volume");
        }
        public void OpenSettings(string section)
        {
            if (_settingsWin != null) { _settingsWin.SelectSection(section); _settingsWin.Activate(); return; }
            _settingsWin = new SettingsWindow(_settings, _store, _openPairing) { Owner = this };
            _settingsWin.Closed += (_, __) => _settingsWin = null;
            _settingsWin.SelectSection(section);
            _settingsWin.Show();
        }
    }
}
