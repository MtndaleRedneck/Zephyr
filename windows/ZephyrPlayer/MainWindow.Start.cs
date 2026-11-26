using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace ZephyrPlayer
{
    /// <summary>The 2.0.2 start lifecycle in the 3.0 shell: an Opening state (new title, empty timeline, spinner),
    /// loading that ends at real content, a failed start (preparation, timeout, libVLC error, end before playing,
    /// 45 s without progress) shown as an error card with Try again and Back, one native-start record per media so a
    /// late event of an older start never touches a newer one, and phone controls that never reach the old movie
    /// while a new start opens.</summary>
    public partial class MainWindow
    {
        internal const string OpenFailedMessage = "Could not open this movie safely. Check the file or phone connection.";
        internal const string NativeFailedMessage = "This movie could not be played. Check the file or phone connection.";

        private bool _opening, _errorShown;
        private (string Url, long? Position, string[] Subtitles, bool OfferStartOver)? _lastStart;
        private Border _loadingPanel = null!, _errorPanel = null!;
        private TextBlock _loadingTitle = null!, _loadingDetail = null!, _errorTitle = null!, _errorMessage = null!;
        private Button _errorRetry = null!, _errorBack = null!;
        private RotateTransform _spinnerTurn = new();
        private readonly DispatcherTimer _stallWatch = new() { Interval = TimeSpan.FromSeconds(1) };
        private NativeStart? _nativeStart;
        private bool _autoSelectSidecar;

        internal LibVLCSharp.Shared.LibVLC LibVlcForTest => _libvlc;
        internal string? LastStartFailure { get; private set; }
        internal int StartCount { get; private set; }
        internal int RetryCount { get; private set; }
        internal int PlayPauseRefusedCount { get; private set; }
        internal TimeSpan NativeStallWindow { get; set; } = TimeSpan.FromSeconds(45);
        internal NativeStart? CurrentNativeStart => _nativeStart;
        /// <summary>Test seams: the start generation and libVLC's state.</summary>
        internal int PlayGeneration => _playGeneration;
        internal LibVLCSharp.Shared.VLCState PlayerStateForTest => _player.State;
        internal (string Url, long? Position, string[] Subtitles)? LastStartArgs => _lastStart is { } s ? (s.Url, s.Position, s.Subtitles) : null;
        internal Func<string, string[], System.Threading.CancellationToken, SafeMediaSource>? PrepareOverride;
        public bool IsLoadingShown => _loadingPanel.Visibility == Visibility.Visible || _errorPanel.Visibility == Visibility.Visible;
        internal bool IsLoadingErrorShown => _errorPanel.Visibility == Visibility.Visible;
        internal string LoadingDetailText => _errorShown ? _errorMessage.Text : _loadingDetail.Text;
        internal bool SeekBarEnabled => SeekBar.IsEnabled;
        public event Action? StartSettled;

        internal sealed class NativeStart
        {
            internal NativeStart(int generation, SafeMediaSource source) { Generation = generation; Source = source; }
            internal int Generation { get; }
            internal SafeMediaSource Source { get; }
            internal bool Waiting = true, Attached, PlayingSeen, Buffered;
            internal long LastBytes, LastProgressTick;
            internal float LastCache = -1;
            internal string? CompletedBy;
            internal EventHandler<EventArgs>? OnPlaying, OnError, OnEnded, OnOpening;
            internal EventHandler<MediaPlayerBufferingEventArgs>? OnBuffering;
        }

        private void InitStartUi()
        {
            // A settings file that cannot be written (disk full, no access) is said once instead of closing the app.
            _settings.SaveFailed += (_, __) => Dispatcher.InvokeAsync(() => ShowSettingsWarning("Your settings could not be saved. The settings file could not be written (the disk may be full or the folder not writable). Zephyr Player keeps working; changes may not be kept after you quit.", "Settings could not be saved"));
            var spinner = new System.Windows.Shapes.Path { Data = Geometry.Parse("M12,3 A9,9 0 1 1 3,12"), Stroke = Orange, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 44, Height = 44, Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _spinnerTurn, HorizontalAlignment = HorizontalAlignment.Center };
            _loadingTitle = Label("", 15, true); _loadingTitle.HorizontalAlignment = HorizontalAlignment.Center; _loadingTitle.Margin = new Thickness(0, 14, 0, 0);
            _loadingDetail = Label("Opening...", 13, false, new SolidColorBrush(Color.FromRgb(0xA7, 0xAF, 0xBC))); _loadingDetail.HorizontalAlignment = HorizontalAlignment.Center; _loadingDetail.Margin = new Thickness(0, 4, 0, 0);
            var lp = new StackPanel(); lp.Children.Add(spinner); lp.Children.Add(_loadingTitle); lp.Children.Add(_loadingDetail);
            _loadingPanel = new Border { Child = lp, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Padding = new Thickness(24) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_loadingPanel, "LoadingPanel");

            _errorTitle = Label("", 16, true); _errorTitle.HorizontalAlignment = HorizontalAlignment.Center; _errorTitle.TextAlignment = TextAlignment.Center; _errorTitle.TextWrapping = TextWrapping.Wrap;
            _errorMessage = Label("", 13, false, new SolidColorBrush(Color.FromRgb(0xA7, 0xAF, 0xBC))); _errorMessage.HorizontalAlignment = HorizontalAlignment.Center; _errorMessage.TextAlignment = TextAlignment.Center; _errorMessage.TextWrapping = TextWrapping.Wrap; _errorMessage.Margin = new Thickness(0, 8, 0, 16);
            _errorRetry = Chip("Try again", (_, __) => _ = RetryLastStartAsync(), double.NaN, 34); _errorRetry.Padding = new Thickness(18, 0, 18, 0);
            _errorRetry.Background = Orange; _errorRetry.Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x10));
            _errorBack = Chip("Back", (_, __) => { HomeRequested?.Invoke(); }, double.NaN, 34); _errorBack.Padding = new Thickness(18, 0, 18, 0); _errorBack.Margin = new Thickness(10, 0, 0, 0);
            System.Windows.Automation.AutomationProperties.SetAutomationId(_errorRetry, "ErrorRetryButton");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_errorBack, "ErrorBackButton");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            buttons.Children.Add(_errorRetry); buttons.Children.Add(_errorBack);
            var ep = new StackPanel(); ep.Children.Add(_errorTitle); ep.Children.Add(_errorMessage); ep.Children.Add(buttons);
            _errorPanel = new Border { Child = ep, Background = PopupCardBrush, BorderBrush = PopupLineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 22, 28, 22), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_errorPanel, "ErrorPanel");
            Overlay.Children.Insert(0, _loadingPanel);
            Overlay.Children.Add(_errorPanel);
            _stallWatch.Tick += (_, __) => CheckNativeStall();
        }

        // ---------- the one playback start ----------

        public async void PlayFromUrl(string url, long? positionSeconds, string[] subtitles, bool offerStartOver = false)
        {
            try { await PlayFromUrlAsync(url, positionSeconds, subtitles, offerStartOver); }
            catch { }   // a failure of the current start already shows as the error card
        }

        /// <summary>Library, /play and /control/play all start here. A failure of the CURRENT start ends loading and shows
        /// the error card before the exception reaches the caller; a superseded or stopped start never touches newer state.</summary>
        public async System.Threading.Tasks.Task PlayFromUrlAsync(string url, long? positionSeconds, string[] subtitles, bool offerStartOver = false)
        {
            var generation = ++_playGeneration;
            DropEndOfMovieSleep();
            HideResumeNotice();
            _startOverGeneration = offerStartOver ? generation : 0;
            _lastStart = (url, positionSeconds, (string[])subtitles.Clone(), offerStartOver);
            StartCount++;
            try { await StartPlaybackAsync(generation, url, positionSeconds, subtitles); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() => FailStart(generation, ex));
                throw;
            }
        }

        private async System.Threading.Tasks.Task StartPlaybackAsync(int generation, string url, long? positionSeconds, string[] subtitles)
        {
            _preparation?.Cancel();
            LastStartFailure = null;
            BeginLoading(url);
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            _preparation = timeout;
            SafeMediaSource source;
            try
            {
                source = await System.Threading.Tasks.Task.Run(() => PrepareOverride != null ? PrepareOverride(url, subtitles, timeout.Token) : PrepareSource(url, subtitles, timeout.Token));
            }
            catch (OperationCanceledException) when (generation == _playGeneration) { throw new TimeoutException("Movie preparation timed out. Check the phone connection."); }
            finally { await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_preparation, timeout)) _preparation = null; }); }
            await Dispatcher.InvokeAsync(() => CompletePreparedPlayback(source, generation, timeout.IsCancellationRequested, url, positionSeconds));
        }

        /// <summary>Settings-write failure: the app's own dark dialog, not the stock light MessageBox.</summary>
        private void ShowSettingsWarning(string text, string caption)
        {
            // A warning about a failed save must never become a crash of its own: if the note cannot be built or shown, playback continues.
            try
            {
                var dlg = FirewallSetup.NoteWindow(text, caption);
                var owner = IsVisible ? this : System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && !ReferenceEquals(w, dlg));
                if (owner != null) { dlg.Owner = owner; dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
                SettingsWarningShown?.Invoke(dlg);
                dlg.ShowDialog();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is System.Runtime.InteropServices.ExternalException) { }
        }

        /// <summary>Test seam: the settings-write warning about to be shown.</summary>
        internal static event Action<Window>? SettingsWarningShown;

        internal SafeMediaSource PrepareSource(string url, string[] subtitles, System.Threading.CancellationToken token)
        {
            // A local movie brings its own sidecar subtitles; phone subtitles come in `subtitles`.
            var all = SafeMediaSource.LocalSidecars(url).Concat(subtitles).Distinct().ToArray();
            _autoSelectSidecar = all.Length > 0;
            var ready = new SafeMediaSource(_libvlc, url, token);
            try { ready.AddSubtitles(all, token); ready.PreparationComplete(); return ready; }
            catch { ready.Dispose(); throw; }
        }

        private void FailStart(int generation, Exception ex)
        {
            if (generation != _playGeneration) return;
            var why = ex is TimeoutException ? "timeout" : "preparation";
            var message = ex is TimeoutException ? ex.Message : OpenFailedMessage;
            if (_safeSource != null) StopPlaybackSource();      // an older movie behind the failed start stops; nothing reopens it
            LastStartFailure = why;
            ShowLoadingError(message);
        }

        // ---------- native start: one record per media handed to libVLC ----------

        private void AttachNativeStart(NativeStart a)
        {
            a.OnPlaying = (_, __) => Dispatcher.BeginInvoke(new Action(() => OnNativePlaying(a)));
            a.OnError = (_, __) => Dispatcher.BeginInvoke(new Action(() => FailNativeStart(a, "native-error")));
            a.OnEnded = (_, __) => Dispatcher.BeginInvoke(new Action(() => FailNativeStart(a, "native-ended")));   // the end before Playing is a failed start
            a.OnOpening = (_, __) => Dispatcher.BeginInvoke(new Action(() => MarkNativeProgress(a)));
            a.OnBuffering = (_, e) => { var cache = e.Cache; Dispatcher.BeginInvoke(new Action(() => OnNativeBuffering(a, cache))); };
            _player.Playing += a.OnPlaying;
            _player.EncounteredError += a.OnError;
            _player.EndReached += a.OnEnded;
            _player.Opening += a.OnOpening;
            _player.Buffering += a.OnBuffering;
            a.Attached = true;
            a.LastProgressTick = Environment.TickCount64;
            a.LastBytes = a.Source.BytesDelivered;
            _nativeStart = a;
            _stallWatch.Stop(); _stallWatch.Start();
        }

        private void OnNativeBuffering(NativeStart a, float cache)
        {
            if (cache != a.LastCache) { a.LastCache = cache; MarkNativeProgress(a); }
            if (cache >= 100f) { a.Buffered = true; if (a.PlayingSeen) OnNativeContent(a, "buffered"); }
        }

        /// <summary>Test seam: libVLC's Buffering percentage reaching a start's own handler path (the event args type cannot be built by a test).</summary>
        internal void RaiseNativeBuffering(NativeStart start, float cache) => Dispatcher.BeginInvoke(new Action(() => OnNativeBuffering(start, cache)));

        /// <summary>Test seam: deliver a libVLC event to a start's OWN attach-time handler, as libVLC's thread would.</summary>
        internal void RaiseNativeEvent(NativeStart start, string kind)
        {
            switch (kind)
            {
                case "playing": start.OnPlaying?.Invoke(_player, EventArgs.Empty); break;
                case "error": start.OnError?.Invoke(_player, EventArgs.Empty); break;
                case "ended": start.OnEnded?.Invoke(_player, EventArgs.Empty); break;
                case "opening": start.OnOpening?.Invoke(_player, EventArgs.Empty); break;
                default: throw new ArgumentException(kind);
            }
        }

        private void DetachNativeHandlers(NativeStart a)
        {
            a.Waiting = false;
            if (!a.Attached) return;
            a.Attached = false;
            _player.Playing -= a.OnPlaying; _player.EncounteredError -= a.OnError; _player.EndReached -= a.OnEnded;
            _player.Opening -= a.OnOpening; _player.Buffering -= a.OnBuffering;
        }

        private void DetachNativeStart()
        {
            _stallWatch.Stop();
            var a = _nativeStart; _nativeStart = null;
            if (a != null) DetachNativeHandlers(a);
        }

        private void OnNativePlaying(NativeStart a)
        {
            if (!a.Waiting || !ReferenceEquals(a, _nativeStart)) return;
            a.PlayingSeen = true;
            MarkNativeProgress(a);
            if (a.Generation != _playGeneration) { _stallWatch.Stop(); DetachNativeHandlers(a); return; }
            if (a.Buffered) OnNativeContent(a, "buffered");
        }

        private void OnNativeContent(NativeStart a, string what)
        {
            if (!a.Waiting || !a.PlayingSeen || !ReferenceEquals(a, _nativeStart)) return;
            _stallWatch.Stop();
            DetachNativeHandlers(a);
            a.CompletedBy = what;
            if (a.Generation != _playGeneration) return;
            LastStartFailure = null;
            SetOpening(false);
            EndLoading();
            AutoSelectSidecar();
            UpdatePosition();
            StartSettled?.Invoke();
        }

        private static void MarkNativeProgress(NativeStart a) { if (a.Waiting) a.LastProgressTick = Environment.TickCount64; }

        private void CheckNativeStall()
        {
            var a = _nativeStart;
            if (a == null || !a.Waiting) { _stallWatch.Stop(); return; }
            if (a.PlayingSeen && _player.Time > 0) { OnNativeContent(a, "clock"); return; }
            var bytes = a.Source.BytesDelivered;
            if (bytes != a.LastBytes) { a.LastBytes = bytes; MarkNativeProgress(a); return; }
            if (Environment.TickCount64 - a.LastProgressTick >= (long)NativeStallWindow.TotalMilliseconds)
            {
                // After libVLC's Playing the movie is libVLC's: a quiet spell is never turned into a failure here.
                if (a.PlayingSeen) OnNativeContent(a, "playing-quiet");
                else FailNativeStart(a, "native-stalled");
            }
        }

        private void FailNativeStart(NativeStart a, string why)
        {
            if (!a.Waiting || !ReferenceEquals(a, _nativeStart) || a.Generation != _playGeneration) return;
            StopPlaybackSource();
            LastStartFailure = why;
            ShowLoadingError(NativeFailedMessage);
        }

        /// <summary>One-shot: the first real subtitle track of a local movie's sidecar is switched on if none is.</summary>
        private void AutoSelectSidecar()
        {
            if (!_autoSelectSidecar) return;
            var tracks = RealTracks(_player.SpuDescription);
            if (tracks.Length == 0) return;
            _autoSelectSidecar = false;
            if (_player.Spu == -1) _player.SetSpu(tracks[0].Id);
        }

        internal static LibVLCSharp.Shared.Structures.TrackDescription[] RealTracks(LibVLCSharp.Shared.Structures.TrackDescription[]? tracks) =>
            (tracks ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>()).Where(t => t.Id != -1).ToArray();

        // ---------- opening / loading / error ----------

        private static string TitleFor(string url)
        {
            var local = PlaybackHistory.LocalPath(url);
            return local != null ? System.IO.Path.GetFileNameWithoutExtension(local) : "Movie from your phone";
        }

        private void BeginLoading(string url)
        {
            var title = TitleFor(url);
            HideError();
            // The Opening state shows the NEW start: its title and an empty timeline, never the previous movie.
            SetOpening(true);
            SeekBar.Value = 0; ElapsedText.Text = "0:00"; RemainingText.Text = "-0:00"; TotalText.Text = "0:00 total";
            MovieTitle.Text = title;
            _loadingTitle.Text = title; _loadingDetail.Text = "Opening...";
            _loadingPanel.Visibility = Visibility.Visible;
            _spinnerTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void SetOpening(bool on)
        {
            _opening = on;
            if (on) _seeking = false;
            if (SeekBar != null) SeekBar.IsEnabled = !on;
        }

        private void EndLoading()
        {
            _spinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            _loadingPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowLoadingError(string message)
        {
            EndLoading();
            ClosePopups();
            SetOpening(false);
            _errorShown = true;
            _errorTitle.Text = _loadingTitle.Text;
            _errorMessage.Text = message;
            _errorRetry.IsEnabled = _lastStart != null;
            _errorPanel.Visibility = Visibility.Visible;
            ControlBar.Visibility = Visibility.Collapsed;
            TopOverlay.Visibility = Visibility.Collapsed;
        }

        private void HideError()
        {
            _errorShown = false;
            _errorPanel.Visibility = Visibility.Collapsed;
            ControlBar.Visibility = Visibility.Visible;
            TopOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>"Try again" re-runs exactly the failed start (same movie, spot and subtitles).</summary>
        internal async System.Threading.Tasks.Task RetryLastStartAsync()
        {
            if (_lastStart is not { } s) return;
            RetryCount++;
            try { await PlayFromUrlAsync(s.Url, s.Position, s.Subtitles, s.OfferStartOver); }
            catch { }
        }

        // ---------- phone controls: never reach the old movie while a new start opens ----------

        internal bool SeekFromPhone(long positionSeconds)
        {
            if (_opening || _player.Length <= 0) return false;
            _player.Time = positionSeconds * 1000;
            return true;
        }

        internal bool PauseFromPhone(bool pause)
        {
            if (_opening) { PlayPauseRefusedCount++; return false; }
            PlayPauseHandedCount++;                       // same seam as the bar and Space (2.0.2 HandPause)
            _player.SetPause(pause);
            UpdatePlayPauseGlyph();
            return true;
        }

        /// <summary>The same path the pop-up, the phone and tests take.</summary>
        public void SelectSubtitle(int trackId, string? label = null)
        {
            _player.SetSpu(trackId);
            if (label == null)
            {
                var tracks = RealTracks(_player.SpuDescription);
                var idx = Array.FindIndex(tracks, t => t.Id == trackId);
                label = trackId == -1 || idx < 0 ? "Off" : (string.IsNullOrWhiteSpace(tracks[idx].Name) ? "Track " + (idx + 1) : tracks[idx].Name);
            }
            ShowOsd("Subtitles: " + label);
        }
    }
}
