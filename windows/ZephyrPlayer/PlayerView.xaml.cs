using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer
{
    /// <summary>The player VIEW hosted by the one ShellWindow (2.0). It is never a
    /// top-level window: leaving it means the shell swaps it out of the tree.</summary>
    public partial class PlayerView : UserControl
    {
        private static readonly SolidColorBrush RingDefault = new(Color.FromRgb(0xF5, 0xF5, 0xF7));
        private static readonly SolidColorBrush IconDefault = new(Color.FromRgb(0xE9, 0xE9, 0xEE));
        private static readonly SolidColorBrush Orange = new(Brand.Orange);
        private static readonly SolidColorBrush OrangeTint = new(Color.FromArgb(0x1A, 0xFF, 0x95, 0x00));
        private static readonly SolidColorBrush WhiteTint = new(Color.FromArgb(0x1A, 0xF5, 0xF5, 0xF7));
        private static readonly SolidColorBrush Dark = new(Color.FromRgb(0x14, 0x14, 0x16));

        // Brushes are shared app-wide; Freeze makes them safe to touch from any
        // thread (each test case runs on its own STA thread - unfrozen statics
        // threw cross-thread Freezable exceptions, CI-observed).
        static PlayerView()
        {
            RingDefault.Freeze();
            IconDefault.Freeze();
            Orange.Freeze();
            OrangeTint.Freeze();
            WhiteTint.Freeze();
            Dark.Freeze();
        }

        private readonly LibVLC _libvlc;
        private readonly MediaPlayer _player;
        private SafeMediaSource? _safeSource;
        // Drawn in the secondary icon's own 24-unit canvas (inside its Viewbox).
        private readonly SecondarySkipHighlight _secondaryBackHighlight=new(){Width=24,Height=24,Backward=true,IsHitTestVisible=false};
        private readonly SecondarySkipHighlight _secondaryForwardHighlight=new(){Width=24,Height=24,IsHitTestVisible=false};
        private readonly PlayerSettings _settings;
        private readonly DispatcherTimer _idleTimer = new();
        private readonly DispatcherTimer _clock = new();
        private readonly DispatcherTimer _sleepTick = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly SleepTimerState _sleep = new();

        private readonly DispatcherTimer _cursorWatch = new() { Interval = TimeSpan.FromMilliseconds(150) };
        [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
        private CursorPoint? _lastScreenCursor;
        private bool _fullscreen;
        private Window? _overlayWindow;
        /// <summary>True while one of the bar's pop-ups is open (it holds the bar up).</summary>
        private bool _menuOpen;
        private Border? _openPopup;

        private bool _seeking;
        private bool _volumeSync;
        private bool _speedSync;
        private double _rate = 1.0;
        private int _subDelayMs;
        private bool _metaDone;
        /// <summary>True from a start's Opening until it settles: the timeline shows the NEW
        /// start (0:00, its own title), never the previous movie's numbers (carry-forward a).</summary>
        private bool _opening;
        private bool _errorShown;
        private (string Url, long? Position, string[] Subtitles)? _lastStart;
        private NativeStart? _mediaStart;
        private int _reflowLevel;
        private readonly PlaybackHistory _history = new();
        private string _currentSource = "";
        private DateTime _lastHistorySave;
        internal void SavePlaybackProgress()
        {
            if (_player.Length > 0 && _player.Time >= 0) _history.Save(_currentSource, _player.Time, _player.Length);
        }

        /// <summary>Fired when the sleep timer paused the movie. The player stays (render
        /// 04: "Pauses the movie when time is up"); nobody navigates on it.</summary>
        public event Action? SleepPaused;
        public event Action? HomeRequested;
        private void OnHome(object sender, RoutedEventArgs e) { ClosePopups(); HomeRequested?.Invoke(); }

        /// <summary>[volume] is accepted for call-site compatibility only: 2.0 volume is
        /// the player's own (owner decision 2) and never reads or writes Windows'
        /// master volume.</summary>
        public PlayerView(LibVLC libvlc, MediaPlayer player, PlayerSettings? settings = null, VolumeService? volume = null)
        {
            InitializeComponent();
            ((Canvas)SecondarySkipBackIcon.Parent).Children.Add(_secondaryBackHighlight);
            ((Canvas)SecondarySkipForwardIcon.Parent).Children.Add(_secondaryForwardHighlight);
            // Seek press/release with handledEventsToo: with IsMoveToPointEnabled the
            // Slider marks a track press handled while it moves the thumb to the point.
            SeekBar.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnSeekDown), true);
            SeekBar.AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler(OnSeekUp), true);
            _libvlc = libvlc;
            _player = player;
            _settings = settings ?? new PlayerSettings();
            Loaded += (_, __) => { Video.MediaPlayer = _player; _cursorWatch.Start(); ShowBarOnce(); HookOverlayKeys(); ApplyLayoutMode(); };
            // The overlay grid loads inside LibVLCSharp's own window, after this view's
            // Loaded: hook that window (keys, first frame) from here.
            Overlay.Loaded += (_, __) => HookOverlayKeys();
            // Unloaded = the shell swapped this view out (library, tray close). Stop
            // every timer; LibVLCSharp hides its overlay window on the same event.
            Unloaded += (_, __) => { ClosePopups(); StopTimers(); };
            IsVisibleChanged += (_, __) => { if (IsVisible) { _cursorWatch.Start(); ShowBarOnce(); } else _cursorWatch.Stop(); };
            _cursorWatch.Tick += (_, __) =>
            {
                if (!IsVisible || !IsLoaded || !GetCursorPos(out var point)) return;
                var old = _lastScreenCursor;
                _lastScreenCursor = point;
                if (old.HasValue && Math.Abs(point.X - old.Value.X) + Math.Abs(point.Y - old.Value.Y) > 2)
                {
                    var local = PointFromScreen(new Point(point.X, point.Y));
                    if (local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight) ShowBarOnce();
                }
            };
            _idleTimer.Interval = TimeSpan.FromSeconds(3);
            _idleTimer.Tick += (_, __) =>
            {
                // Auto-hide (item 65): bar and top overlay fade over 250 ms after 3 s idle.
                // Kept up while the pointer is on the bar, while seeking, while a pop-up is
                // open, and while nothing is playing (paused, ended: review blocker 3 - a
                // hidden bar strands phone-only viewers with no visible controls).
                if (!BarMayHide) return;
                FadeBar(0);
                if (IsFullscreen) Overlay.Cursor = Cursors.None;
                _idleTimer.Stop();
            };
            _player.Paused += (_, __) => Dispatcher.BeginInvoke(() =>
            {
                // Phone-pause reveals the bar (review evidence): hiding it while
                // paused strands phone-only viewers with no visible controls.
                RevealBar();
                _idleTimer.Stop(); // never auto-hide while paused
                UpdatePlayPauseGlyph();
            });
            // Any Playing (also a resume after pause). It never touches the loading
            // state: only the start that is waiting for its OWN Playing ends loading
            // (see NativeStart below), so an old movie's Playing cannot clear a newer
            // start's spinner.
            _player.Playing += (_, __) => Dispatcher.BeginInvoke(() =>
            {
                TryAutoSelectSidecar();
                if (IsLoaded) { _idleTimer.Stop(); _idleTimer.Start(); } // resume the hide countdown
                RefreshCaptionsUi();
                UpdatePlayPauseGlyph();
            });
            _player.ESSelected += (_, __) => Dispatcher.BeginInvoke(RefreshCaptionsUi);
            _player.ESAdded += (_, ev) => { if (ev.Type == TrackType.Text) Dispatcher.BeginInvoke(TryAutoSelectSidecar); };
            // Native start: Playing / EncounteredError / EndReached / Buffering handlers
            // are attached per start (AttachNativeStart), never here, so each one is
            // tied to the media it was attached for.
            _stallWatch.Interval = TimeSpan.FromSeconds(1);
            _stallWatch.Tick += (_, __) => CheckNativeStall();
            _clock.Interval = TimeSpan.FromMilliseconds(500);
            _clock.Tick += (_, __) => UpdatePosition();
            _sleepTick.Tick += (_, __) => SleepTick();

            // Settings changes apply live: bar style AND the Boost (decoder gain) at the
            // current slider value, on the playing movie - never a reopen.
            _settings.Changed += (_, __) => Dispatcher.InvokeAsync(OnSettingsChanged);
            _settings.WriteFailed += (_, __) => Dispatcher.InvokeAsync(() => ShowSettingsWarning("Saved settings are unreadable. The original file has been preserved, and changes cannot be saved until it is repaired.", "Settings could not be saved"));

            // Remembered volume restored on open: the player's own 0-100 %, with the
            // Settings boost (100-200 %) applied on top as extra decoder gain.
            _volumeSync = true;
            VolumeSlider.Value = _settings.LastVolume;
            _volumeSync = false;
            ApplyPlayerGain();
            ApplyBarStyle();
            RefreshVolumeUi();
            RefreshSpeedUi();
            RefreshSleepUi();
            RefreshSubtitleSizeUi();
            UpdatePlayPauseGlyph();
        }

        private void ShowSettingsWarning(string text, string caption)
        {
            var owner = Window.GetWindow(this);
            if (owner != null && owner.IsVisible) MessageBox.Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(text, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private int _playGeneration;
        private System.Threading.CancellationTokenSource? _preparation;
        /// <summary>Best-effort teardown of a replaced source off the UI thread.
        /// A Dispose failure is logged by exception type only, and nothing
        /// guarantees this runs before process exit.</summary>
        private static void DisposeReplacedSource(SafeMediaSource source)
        {
            try { source.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Replaced media source cleanup failed: " + ex.GetType().Name); }
        }

        public void StopPlaybackSource()
        {
            ++_playGeneration; _preparation?.Cancel();
            DropEndOfMovieSleep();
            DetachNativeStart();
            EndLoading();
            SetOpening(false);
            DropPendingSettle();
            _autoSelectSidecar = false;
            _safeSource?.Abort(); _player.Stop(); _player.Media=null;
            var old = _safeSource; _safeSource = null;
            // Teardown off the caller thread: disposing the media waits on native
            // decoder threads and provider closes, and a blocked close/read must
            // never stall stop/cleanup on the UI.
            if (old != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => DisposeReplacedSource(old));
        }
        internal const string OpenFailedMessage = "Could not open this movie safely. Check the file or phone connection.";
        internal const string NativeFailedMessage = "This movie could not be played. Check the file or phone connection.";

        /// <summary>Local (library) start. Failure handling lives in the shared
        /// PlayFromUrlAsync path, so nothing is left to do here.</summary>
        public async void PlayFromUrl(string url,long? positionSeconds,string[] subtitles)
        {
            try { await PlayFromUrlAsync(url,positionSeconds,subtitles); }
            catch { }
        }

        /// <summary>The one playback start for library, /play and /control/play.
        /// A failure of the CURRENT start ends loading and shows the error surface
        /// before the exception reaches the caller (PlaybackServer answers 400/408).
        /// A start that was superseded or stopped never touches the newer state.</summary>
        public async System.Threading.Tasks.Task PlayFromUrlAsync(string url,long? positionSeconds,string[] subtitles)
        {
            var generation=++_playGeneration;
            DropEndOfMovieSleep(); // a new start leaves the movie "End of this movie" was set on
            // "Try again" on the error state re-runs exactly this start (item 68).
            _lastStart = (url, positionSeconds, (string[])subtitles.Clone());
            StartCount++;
            try { await StartPlaybackAsync(generation,url,positionSeconds,subtitles); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(()=>FailStart(generation,ex));
                throw;
            }
        }

        /// <summary>Test seams (InternalsVisibleTo): replace source preparation, and
        /// read why the last start failed ("preparation", "timeout", "native-error",
        /// "native-ended", "native-timeout"; null after a start that played).</summary>
        internal Func<string,string[],System.Threading.CancellationToken,SafeMediaSource>? PrepareOverride;
        internal string? LastStartFailure { get; private set; }
        internal int PlayGeneration => _playGeneration;
        internal object? CurrentMedia => _safeSource?.Media;
        /// <summary>No progress at all for this long while a start waits for Playing
        /// fails it (see CheckNativeStall for why 45 s).</summary>
        internal TimeSpan NativeStallWindow { get; set; } = DefaultNativeStallWindow;
        internal static readonly TimeSpan DefaultNativeStallWindow = TimeSpan.FromSeconds(45);
        internal TimeSpan NativeStallPoll { get => _stallWatch.Interval; set => _stallWatch.Interval = value; }
        internal NativeStart? CurrentNativeStart => _nativeStart;
        /// <summary>Test seam: deliver a native event to a start's OWN attach-time
        /// handler, exactly as libVLC's event thread would (the handler only queues
        /// work on the dispatcher). kind: playing, error, ended, buffering, opening.</summary>
        internal void RaiseNativeEvent(NativeStart start, string kind, float cache = 0)
        {
            switch (kind)
            {
                case "playing": start.OnPlaying?.Invoke(_player, EventArgs.Empty); break;
                case "error": start.OnError?.Invoke(_player, EventArgs.Empty); break;
                case "ended": start.OnEnded?.Invoke(_player, EventArgs.Empty); break;
                case "opening": start.OnOpening?.Invoke(_player, EventArgs.Empty); break;
                case "vout": start.OnVout?.Invoke(_player, (MediaPlayerVoutEventArgs)Activator.CreateInstance(typeof(MediaPlayerVoutEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new object[] { (int)cache }, null)!); break;
                case "finished": start.OnFinished?.Invoke(_player, EventArgs.Empty); break;
                case "buffering": start.OnBuffering?.Invoke(_player, (MediaPlayerBufferingEventArgs)Activator.CreateInstance(typeof(MediaPlayerBufferingEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new object[] { cache }, null)!); break;
                default: throw new ArgumentException(kind);
            }
        }

        internal SafeMediaSource PrepareSource(string url,string[] subtitles,System.Threading.CancellationToken token)
        {
            var ready=new SafeMediaSource(_libvlc,url,token);
            try { ready.AddSubtitles(subtitles,token); ready.PreparationComplete(); return ready; }
            catch { ready.Dispose(); throw; }
        }

        private void FailStart(int generation,Exception ex)
        {
            // An older request that fails after a newer start began is not ours to show.
            if (generation!=_playGeneration) return;
            var why = ex is TimeoutException ? "timeout" : "preparation";
            var message = ex is TimeoutException ? ex.Message : OpenFailedMessage;
            // An older movie still playing behind the failed start stops here: the error
            // state replaces it (the user asked for the new one), and nothing reopens it.
            if (_safeSource != null) StopPlaybackSource();
            LastStartFailure = why;
            ShowLoadingError(message);
        }

        // ---------- native start: one record per media handed to libVLC ----------

        /// <summary>One native start: the media handed to libVLC for one generation and
        /// the event handlers attached FOR it at handoff. libVLC raises its events on
        /// its own thread; each handler captured this record when it was attached, so
        /// an event queued by an earlier start (Playing, error, end, buffering) carries
        /// the earlier record and is ignored once a newer start exists. Nothing reads
        /// "the current generation" at event time.</summary>
        internal sealed class NativeStart
        {
            internal NativeStart(int generation, SafeMediaSource source) { Generation = generation; Source = source; }
            internal int Generation { get; }
            internal SafeMediaSource Source { get; }
            /// <summary>True from handoff until its own Playing, a failure, or a stop.</summary>
            internal bool Waiting = true;
            internal bool Attached;
            internal long LastBytes;
            internal float LastCache = -1;
            internal long LastProgressTick;
            internal string LastProgress = "handoff";
            internal int ProgressMarks;
            /// <summary>libVLC said Playing (its demuxer opened); the picture may still be
            /// buffering. Loading ends on the signals listed at OnNativeContent.</summary>
            internal bool PlayingSeen;
            internal bool Buffered;
            internal string? CompletedBy;
            internal EventHandler<EventArgs>? OnPlaying, OnError, OnEnded, OnOpening;
            /// <summary>EndReached for this start's media for as long as libVLC holds it
            /// (sleep "End of this movie"); detached when the media is replaced or stopped.</summary>
            internal EventHandler<EventArgs>? OnFinished;
            internal EventHandler<MediaPlayerBufferingEventArgs>? OnBuffering;
            internal EventHandler<MediaPlayerVoutEventArgs>? OnVout;
        }
        private NativeStart? _nativeStart;
        private readonly DispatcherTimer _stallWatch = new();

        private void AttachNativeStart(NativeStart a)
        {
            a.OnPlaying = (_, __) => Dispatcher.BeginInvoke(() => OnNativePlaying(a));
            a.OnError = (_, __) => Dispatcher.BeginInvoke(() => FailNativeStart(a, "native-error"));
            a.OnEnded = (_, __) => Dispatcher.BeginInvoke(() => FailNativeStart(a, "native-ended"));
            a.OnOpening = (_, __) => Dispatcher.BeginInvoke(() => MarkNativeProgress(a, "opening"));
            a.OnBuffering = (_, e) => { var cache = e.Cache; Dispatcher.BeginInvoke(() =>
            {
                if (cache != a.LastCache) { a.LastCache = cache; MarkNativeProgress(a, "buffering " + cache.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"); }
                if (cache >= 100f) { a.Buffered = true; if (a.PlayingSeen) OnNativeContent(a, "buffered"); }
            }); };
            // A video output appearing is progress, not content: libVLC 3 opens it
            // (black) before buffering ends and the first picture shows.
            a.OnVout = (_, e) => { var count = e.Count; Dispatcher.BeginInvoke(() => { if (count > 0) MarkNativeProgress(a, "video output"); }); };
            _player.Playing += a.OnPlaying;
            _player.EncounteredError += a.OnError;
            _player.EndReached += a.OnEnded;
            _player.Opening += a.OnOpening;
            _player.Buffering += a.OnBuffering;
            _player.Vout += a.OnVout;
            a.OnFinished = (_, __) => Dispatcher.BeginInvoke(() => OnMediaFinished(a));
            _player.EndReached += a.OnFinished;
            _mediaStart = a;
            a.Attached = true;
            a.LastProgressTick = Environment.TickCount64;
            a.LastBytes = a.Source.BytesDelivered;
            _nativeStart = a;
            _stallWatch.Stop(); _stallWatch.Start();
        }

        /// <summary>Its handlers leave libVLC's event lists. An event libVLC already
        /// dispatched still reaches the handler, which carries this record and finds
        /// it no longer waiting.</summary>
        private void DetachNativeHandlers(NativeStart a)
        {
            a.Waiting = false;
            if (!a.Attached) return;
            a.Attached = false;
            // The delegates themselves are kept: a late delivery (or the test seam)
            // still runs them, and they find their record no longer waiting.
            _player.Playing -= a.OnPlaying;
            _player.EncounteredError -= a.OnError;
            _player.EndReached -= a.OnEnded;
            _player.Opening -= a.OnOpening;
            _player.Buffering -= a.OnBuffering;
            _player.Vout -= a.OnVout;
        }

        private void DetachNativeStart()
        {
            _stallWatch.Stop();
            var a = _nativeStart; _nativeStart = null;
            if (a != null) DetachNativeHandlers(a);
            var m = _mediaStart; _mediaStart = null;
            if (m?.OnFinished != null) _player.EndReached -= m.OnFinished;
        }

        /// <summary>This start's own Playing. libVLC 3 says Playing when its demuxer has
        /// opened, before the first picture: a slow phone can still be buffering for a
        /// while (CI run 41033075025: about 0.7 s of an all-black video area between
        /// Playing and the first frame on a dribbling source). So Playing alone does not
        /// end loading; see OnNativeContent for the exact signals that do. A start that
        /// played while a newer one is preparing leaves the newer spinner up.</summary>
        private void OnNativePlaying(NativeStart a)
        {
            if (!a.Waiting || !ReferenceEquals(a, _nativeStart)) return;
            a.PlayingSeen = true;
            MarkNativeProgress(a, "playing");
            if (a.Generation != _playGeneration)
            {
                // A newer start is preparing; this one is done waiting and its handlers go.
                _stallWatch.Stop();
                DetachNativeHandlers(a);
                return;
            }
            if (a.Buffered) OnNativeContent(a, "buffered");
        }
        /// <summary>Where loading ends, for the newest start only. Exact signals (nothing
        /// here observes a rendered picture): after this start's own Playing, either
        /// libVLC reports Buffering 100 % ("buffered") or the movie clock is past 0 at a
        /// 1-second poll ("clock"); or, if neither comes, 45 s (NativeStallWindow) after
        /// the last progress with Playing already seen ("playing-quiet"), a fallback that
        /// ends loading without any picture signal. Before Playing, the same 45 s of no
        /// progress is a failure instead (CheckNativeStall).</summary>

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
            UpdatePosition();
            RaiseStartSettled();
        }

        /// <summary>The newest start shows its own content now: its first Playing, or
        /// the error panel. The shell releases the library it kept drawn underneath.</summary>
        public event Action? StartSettled;

        // The shell releases the library it holds under the player on StartSettled.
        // LibVLCSharp's overlay window (where the loading panel, bar and error state
        // live) is a separate layered window: before its FIRST frame is drawn it shows
        // nothing, so a start that settled before that frame uncovered the bare black
        // video host (CI run 41047779027: three all-black sampled frames in the CP2
        // library->player phase, the start settling before the overlay's first paint).
        // So the settle waits for the overlay window's ContentRendered, with a 1.5 s
        // fallback; a newer start or a stop drops a pending settle.
        private bool _overlayRendered;
        private bool _settlePending;
        private int _settleGeneration;
        private void RaiseStartSettled()
        {
            HookOverlayKeys();
            if (_overlayRendered || !IsLoaded) { _settlePending = false; StartSettled?.Invoke(); return; }
            _settlePending = true;
            var generation = ++_settleGeneration;
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            fallback.Tick += (_, __) => { fallback.Stop(); if (generation == _settleGeneration) FlushStartSettled(); };
            fallback.Start();
        }
        private void FlushStartSettled()
        {
            if (!_settlePending) return;
            _settlePending = false;
            StartSettled?.Invoke();
        }
        private void DropPendingSettle() { _settlePending = false; _settleGeneration++; }
        private System.Threading.Tasks.TaskCompletionSource<bool>? _overlayFirstFrame;
        /// <summary>Completes once the overlay window has drawn its first frame (at most
        /// 1 s), or at once when it already has or this view is not on screen.</summary>
        private System.Threading.Tasks.Task WaitOverlayFirstFrameAsync() => Dispatcher.Invoke(() =>
        {
            HookOverlayKeys();
            if (_overlayRendered || PresentationSource.FromVisual(this) == null) return System.Threading.Tasks.Task.CompletedTask;
            _overlayFirstFrame ??= new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            return (System.Threading.Tasks.Task)System.Threading.Tasks.Task.WhenAny(_overlayFirstFrame.Task, System.Threading.Tasks.Task.Delay(1000));
        });
        /// <summary>Test seam: the overlay window has drawn its first frame.</summary>
        internal bool OverlayRendered => _overlayRendered;

        /// <summary>While the shell keeps the library drawn underneath (library ->
        /// player), this view's own surfaces are transparent so WPF shows the library,
        /// not black, until the start settles.</summary>
        internal void SetSwitchTransparent(bool on)
        {
            Background = on ? Brushes.Transparent : Brushes.Black;
            Video.Background = on ? Brushes.Transparent : Brushes.Black;
        }

        private void MarkNativeProgress(NativeStart a, string what)
        {
            if (!a.Waiting || !ReferenceEquals(a, _nativeStart)) return;
            a.LastProgressTick = Environment.TickCount64; a.LastProgress = what; a.ProgressMarks++;
        }

        /// <summary>The start contract (CP1 repair 2). A start fails on a real signal
        /// only: libVLC's EncounteredError, EndReached before Playing, or NO progress
        /// at all for NativeStallWindow (45 s). Progress = this source handed libVLC
        /// more bytes, or libVLC reported Opening or a new Buffering percent for this
        /// start. A slow phone that keeps delivering is never stopped, however long it
        /// takes. Why 45 s: a phone read that delivers nothing for 15 s already fails
        /// in PeerRangeStream (its per-read deadline), and libVLC reports that read
        /// failure as an error or an end - the real signal, at about 15 s. The stall
        /// window is only the backstop for libVLC itself going quiet (asking for no
        /// bytes and reporting nothing), so it is three per-read deadlines: it can
        /// never fire before the source's own deadline could have. libVLC's default
        /// caching is 0.3 s (file) / 1 s (network), so a healthy decoder that has
        /// bytes asks for more far inside that window.</summary>
        private void CheckNativeStall()
        {
            var a = _nativeStart;
            if (a == null || !a.Waiting) { _stallWatch.Stop(); return; }
            if (a.PlayingSeen && _player.Time > 0) { OnNativeContent(a, "clock"); return; }
            var bytes = a.Source.BytesDelivered;
            if (bytes != a.LastBytes) { a.LastBytes = bytes; MarkNativeProgress(a, "bytes " + bytes); return; }
            if (Environment.TickCount64 - a.LastProgressTick >= (long)NativeStallWindow.TotalMilliseconds)
            {
                // After libVLC's Playing the movie is libVLC's: a quiet spell then is
                // never turned into a failure here (its own error/end still are); the
                // spinner simply gives way.
                if (a.PlayingSeen) OnNativeContent(a, "playing-quiet");
                else FailNativeStart(a, "native-stalled");
            }
        }

        private void FailNativeStart(NativeStart a, string why)
        {
            // Only the start this handler was attached for, only while it waits, and
            // only while no newer start exists (a newer preparation replaces it anyway).
            if (!a.Waiting || !ReferenceEquals(a, _nativeStart) || a.Generation != _playGeneration) return;
            StopPlaybackSource(); // the failed media goes; any older completion is invalidated
            LastStartFailure=why;
            ShowLoadingError(NativeFailedMessage);
        }

        private async System.Threading.Tasks.Task StartPlaybackAsync(int generation,string url,long? positionSeconds,string[] subtitles)
        {
            _preparation?.Cancel();
            LastStartFailure=null;
            BeginLoading(url);
            // Local movie with its own sidecar subtitles: shown by default, like VLC.
            // Phone starts keep libVLC's own default.
            _autoSelectSidecar = subtitles.Length > 0 && PlaybackHistory.LocalPath(url) != null;
            using var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            _preparation=timeout;
            SafeMediaSource source;
            var prepare=PrepareOverride;
            try {source=await System.Threading.Tasks.Task.Run(() => prepare!=null ? prepare(url,subtitles,timeout.Token) : PrepareSource(url,subtitles,timeout.Token));}
            catch(OperationCanceledException) when(generation==_playGeneration) {throw new TimeoutException("Movie preparation timed out. Check the phone connection.");}
            finally {await Dispatcher.InvokeAsync(()=>{if(ReferenceEquals(_preparation,timeout))_preparation=null;});}
            // libVLC's own video window (black until the first picture) goes up inside the
            // video host as soon as the media is handed over, OVER the library picture the
            // shell holds there. The overlay window's loading panel must be drawn first, or
            // a fast local start shows bare black (CI run 41052665982: the vout window was
            // up at +516 ms, the overlay's first frame at +672 ms). First start only: the
            // overlay window is created once per view.
            await WaitOverlayFirstFrameAsync();
            await Dispatcher.InvokeAsync(()=>CompletePreparedPlayback(source,generation,timeout.IsCancellationRequested,url,positionSeconds));
        }
        private void CompletePreparedPlayback(SafeMediaSource source,int generation,bool canceled,string url,long? positionSeconds)
        {
            if(generation!=_playGeneration) {source.Dispose();throw new OperationCanceledException("Playback preparation was superseded or stopped.");}
            if(canceled) {source.Dispose();throw new TimeoutException("Movie preparation timed out. Check the phone connection.");}
            SavePlaybackProgress();
            _currentSource = url;
            MovieTitle.Text = TitleFor(url);
            _metaDone = false; MovieMeta.Text = "";
            _subDelayMs = 0; SubDelayText.Text = PlayerBarLogic.FormatDelay(0);
            _lastHistorySave = DateTime.MinValue;
            // The player's own volume (slider 0-100 x boost) and its own mute carry
            // across the start; Windows' master volume is never touched.
            ApplyPlayerGain();
            _player.Mute = _playerMute;
            var replaced = _safeSource;
            // The previous start's handlers leave BEFORE its media stops, and this
            // start's are attached after: no event of the old media is ever seen by a
            // handler of this one.
            DetachNativeStart();
            replaced?.Abort();
            _player.Stop();
            _player.Media=null;
            _safeSource = source;
            AttachNativeStart(new NativeStart(generation, source));
            _player.Play(source.Media);
            _player.Mute = _playerMute; // libVLC resets mute on a new Media
            // The speed the bar shows (_rate) carries across starts. libVLC 3 keeps the
            // rate on the MediaPlayer and a new input inherits it; if this player ever
            // reports another rate for the new media, the decoder is set back to what
            // the bar shows (no reopen). RateAtHandoff records what libVLC reported.
            RateAtHandoff = _player.Rate;
            if (Math.Abs(RateAtHandoff - (float)_rate) > 0.001f) { _player.SetRate((float)_rate); RateReapplyCount++; }
            // The replaced source tears down off the UI thread (see StopPlaybackSource).
            if (replaced != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => DisposeReplacedSource(replaced));
            if (positionSeconds.HasValue && positionSeconds.Value > 0)
                _player.Time = positionSeconds.Value * 1000;
            _clock.Start();
            // Phone-started playback shows the bar once (review blocker 3): the PC
            // mouse may never move, so the idle timer alone can't reveal it.
            RevealBar();
            _idleTimer.Stop();
            _idleTimer.Start();
            UpdatePlayPauseGlyph();
            RefreshSpeedUi();
        }

        // The player's own mute as last APPLIED. libvlc's Mute read-back is
        // unreliable with no media loaded or no audio device (it returns false
        // regardless of what was set), so this field is the truth the UI and the
        // phone read. Every assignment below keeps both in step.
        private bool _playerMute;
        /// <summary>Test seam (InternalsVisibleTo): the tracked player mute.</summary>
        internal bool TrackedPlayerMute { get => _playerMute; set => _playerMute = value; }

        private static string TitleFor(string url)
        {
            var local = PlaybackHistory.LocalPath(url);
            return local != null ? System.IO.Path.GetFileNameWithoutExtension(local) : "Movie from your phone";
        }

        private void UpdatePosition()
        {
            if (DateTime.UtcNow - _lastHistorySave >= TimeSpan.FromSeconds(5))
            { SavePlaybackProgress(); _lastHistorySave = DateTime.UtcNow; }
            UpdatePlayPauseGlyph();
            // While a new start is opening, the timeline belongs to it: an older movie may
            // still be playing underneath until the new one is handed to libVLC, and its
            // numbers must not show (carry-forward a).
            if (_opening) return;
            // 2.0: no Windows master poll. Volume and mute are the player's own.
            if (!_seeking)
            {
                var length = _player.Length;
                if (length > 0)
                {
                    var time = Math.Max(0, _player.Time);
                    SeekBar.Value = 1000.0 * time / length;
                    ElapsedText.Text = Format(time);
                    TotalText.Text = Format(length) + " total";
                    RemainingText.Text = "-" + Format(Math.Max(0, length - time));
                    if (!_metaDone) UpdateMeta(length);
                }
            }
        }

        /// <summary>Back to an empty timeline (0:00, -0:00, 0:00 total, thumb at the start).</summary>
        private void ResetTimeline()
        {
            SeekBar.Value = 0;
            ElapsedText.Text = "0:00";
            RemainingText.Text = "-0:00";
            TotalText.Text = "0:00 total";
            MovieMeta.Text = "";
            _metaDone = false;
        }

        private void UpdateMeta(long length)
        {
            string? year = null;
            try { year = _safeSource?.Media?.Meta(MetadataType.Date); } catch { year = null; }
            MovieMeta.Text = PlayerBarLogic.MetaLine(year, length);
            _metaDone = true;
        }

        private void UpdatePlayPauseGlyph()
        {
            var playing = _player.IsPlaying;
            PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string Format(long ms)
        {
            var s = ms / 1000;
            return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        // The last REAL cursor position: WPF re-raises MouseMove for a stationary
        // cursor when content repaints under it (video frames!), and treating those
        // as movement would pin the bar visible for the whole movie.
        private System.Windows.Point? _lastMousePos;

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (_lastMousePos.HasValue && pos == _lastMousePos.Value) return;
            _lastMousePos = pos;
            RevealBar();
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        private static string ShortSkip(int seconds) => PlayerSettings.FormatSkip(seconds);
        /// <summary>The one key map (P0-4). The shell routes its PreviewKeyDown here
        /// and so does LibVLCSharp's overlay window (where the bar lives), so a
        /// click on any bar control never strands the keyboard: bar controls are
        /// Focusable=False and both windows feed this same handler.</summary>
        public void HandleKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) != 0) return;
            // Escape closes an open pop-up first.
            if (key == Key.Escape && _menuOpen) { ClosePopups(); e.Handled = true; return; }
            ShowBarOnce();
            switch (key)
            {
                case Key.Space: OnPlayPause(this, new RoutedEventArgs()); break;
                case Key.Left: OnSkipBack(this, new RoutedEventArgs()); break;
                case Key.Right: OnSkipForward(this, new RoutedEventArgs()); break;
                case Key.Up: StepVolume(+VolumeStep); break;
                case Key.Down: StepVolume(-VolumeStep); break;
                case Key.F: SetFullscreen(!IsFullscreen); break;
                case Key.Escape: if (IsFullscreen) SetFullscreen(false); else return; break;
                case Key.M: OnMute(this, new RoutedEventArgs()); break;
                default: return;
            }
            e.Handled = true;
        }

        /// <summary>Up/Down volume step, in slider percent.</summary>
        internal const int VolumeStep = 5;
        internal void StepVolume(int delta) => VolumeSlider.Value = Math.Clamp(Math.Round(VolumeSlider.Value) + delta, 0, 100);

        /// <summary>Keys pressed while LibVLCSharp's overlay window has focus (it does
        /// after any click on the bar) reach the same key map. The overlay window
        /// exists once its VideoView template is applied; hook it once.</summary>
        private void HookOverlayKeys()
        {
            var overlay = Window.GetWindow(Overlay);
            if (overlay == null || ReferenceEquals(overlay, Window.GetWindow(this)) || ReferenceEquals(overlay, _overlayWindow)) return;
            _overlayWindow = overlay;
            overlay.PreviewKeyDown += (_, e) => { if (IsLoaded) HandleKey(e); };
            overlay.ContentRendered += (_, __) =>
            {
                _overlayRendered = true;
                _overlayFirstFrame?.TrySetResult(true);
                // One more pass so the frame is on screen before the library leaves.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushStartSettled));
            };
        }

        /// <summary>Belt and braces for every leave/hide path: hide LibVLCSharp's
        /// overlay window right now (Unloaded also hides it, a moment later) and
        /// stop the timers. Win32 never hides an owned window with its owner.</summary>
        public void HideOverlayWindow()
        {
            ClosePopups();
            StopTimers();
            var overlay = Window.GetWindow(Overlay);
            if (overlay != null && !ReferenceEquals(overlay, Window.GetWindow(this))) overlay.Hide();
        }

        private void StopTimers()
        {
            _idleTimer.Stop();
            _clock.Stop();
            _cursorWatch.Stop();
        }

        /// <summary>Test and teardown seam (the old window's Close): stop playback,
        /// stop timers.</summary>
        public void ReleasePlayback()
        {
            StopTimers();
            _sleepTick.Stop();
            StopPlaybackSource();
        }

        private void OnVideoDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ControlBar.IsMouseOver || IsInsideOpenPopup(e.OriginalSource as DependencyObject)) return;
            SetFullscreen(!IsFullscreen); e.Handled = true;
        }

        // ---------- seek: click-to-seek and drag (item 50) ----------

        private void OnSeekDown(object sender, MouseButtonEventArgs e)
        {
            if (_opening) { _seeking = false; return; } // Opening: no seek reaches the old movie
            _seeking = true;
        }

        /// <summary>IsMoveToPointEnabled (ZSeekSlider) moves the thumb to the clicked point
        /// on mouse-down; the seek itself happens here, on release (click or drag end).</summary>
        private void OnSeekUp(object sender, MouseButtonEventArgs e)
        {
            if (!_seeking) return;
            _seeking = false;
            if (_opening) return;
            CommitSeek();
        }

        private void CommitSeek()
        {
            // During Opening the media libVLC holds is the OLD movie (or the new one not
            // yet showing): nothing is written to it and the zeroed timeline stays.
            if (_opening || _player.Length <= 0) return;
            LastSeekFraction = SeekBar.Value / 1000.0;
            _player.Position = (float)LastSeekFraction;
            var t = (long)(LastSeekFraction * _player.Length);
            ElapsedText.Text = Format(t);
            RemainingText.Text = "-" + Format(Math.Max(0, _player.Length - t));
        }

        /// <summary>The phone's seek (/control/seek). Refused while a new start opens, for
        /// the same reason as the bar: the media libVLC holds then is the old movie.</summary>
        internal bool SeekFromPhone(long positionSeconds)
        {
            if (_opening || _player.Length <= 0) return false;
            _player.Time = positionSeconds * 1000;
            return true;
        }

        /// <summary>Test seam: the fraction the last click/drag seek handed to libVLC.</summary>
        internal double LastSeekFraction { get; private set; } = -1;

        private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // While the thumb is held, the elapsed label previews where it will land.
            // Opening: the new start's timeline stays at zero whatever moved the thumb.
            if (_opening) { if (SeekBar.Value != 0) SeekBar.Value = 0; return; }
            if (!_seeking || _player is null || _player.Length <= 0) return;
            ElapsedText.Text = Format((long)(SeekBar.Value / 1000.0 * _player.Length));
        }

        // ---------- play / pause / skips ----------

        private void OnPlayPause(object sender, RoutedEventArgs e)
        {
            // Opening: the media libVLC holds is the OLD movie behind the new title and
            // zero timeline. Nothing reaches it, and nothing is saved for the new movie
            // either (no deferred play/pause); the press simply does nothing.
            if (_opening) { PlayPauseRefusedCount++; return; }
            if (_player.IsPlaying) HandPause(true);
            else if (_player.State == VLCState.Paused) HandPause(false);
            UpdatePlayPauseGlyph();
        }

        /// <summary>The phone's /control/pause and /control/resume. Refused while a new
        /// start opens, like the bar and Space (the media libVLC holds then is the old
        /// movie); not queued for the new movie.</summary>
        internal bool PauseFromPhone(bool pause)
        {
            if (_opening) { PlayPauseRefusedCount++; return false; }
            HandPause(pause);
            UpdatePlayPauseGlyph();
            return true;
        }

        private void HandPause(bool pause)
        {
            PlayPauseHandedCount++;
            _player.SetPause(pause);
        }

        /// <summary>Test seams: play/pause requests handed to libVLC, and requests refused
        /// because a new start was opening.</summary>
        internal int PlayPauseHandedCount { get; private set; }
        internal int PlayPauseRefusedCount { get; private set; }

        private void OnSkipBack(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(false, true), SkipBackSpin, true);

        private void OnSkipForward(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(false, false), SkipForwardSpin, false);

        private void OnSecondarySkipBack(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(true, true), SecondaryBackSpin, true);

        private void OnSecondarySkipForward(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(true, false), SecondaryForwardSpin, false);

        /// <summary>Test seam: what the last skip press animated ("spin", "light", "flash").</summary>
        internal string LastPressEffects { get; private set; } = "";

        private void Skip(int deltaSeconds, RotateTransform? spin, bool backward)
        {
            // Nothing loaded = nothing to skip: no time change, no spin, no flash. During
            // Opening the loaded media is the old movie: same answer.
            if (_opening || _player.Length <= 0) return;
            _player.Time = Math.Clamp(_player.Time + deltaSeconds * 1000L, 0, _player.Length);
            var secondary = spin == SecondaryBackSpin || spin == SecondaryForwardSpin;
            var effects = new System.Collections.Generic.List<string>();
            // Press animations (item 54) live behind "Animate on press"; the colour flash
            // (item 55) behind "Flash on press". Rotate only the ring; the label stays upright.
            if (_settings.AnimateOnPress)
            {
                if (spin != null && !secondary)
                {
                    spin.BeginAnimation(RotateTransform.AngleProperty, null);
                    spin.Angle = 0;
                    var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), FillBehavior = FillBehavior.Stop };
                    anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    anim.KeyFrames.Add(new SplineDoubleKeyFrame(backward ? -360 : 360, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new KeySpline(.4, 0, .2, 1)));
                    spin.BeginAnimation(RotateTransform.AngleProperty, anim);
                    effects.Add("spin");
                }
                else if (secondary)
                {
                    var highlight = backward ? _secondaryBackHighlight : _secondaryForwardHighlight;
                    highlight.Start(((SolidColorBrush)(backward ? SecondarySkipBackIcon : SecondarySkipForwardIcon).Stroke).Color);
                    effects.Add("light");
                }
            }
            else { _secondaryBackHighlight.Stop(); _secondaryForwardHighlight.Stop(); }
            if (_settings.ButtonFlash) { Flash(backward, secondary); effects.Add("flash"); }
            LastPressEffects = string.Join(",", effects);
        }

        private void Flash(bool backward, bool secondary)
        {
            // Animate only the pressed glyph. The number keeps its selected color.
            var path = secondary ? (backward ? SecondarySkipBackIcon : SecondarySkipForwardIcon) : (backward ? SkipBackRing : SkipForwardRing);
            var head = secondary ? (backward ? SecondarySkipBackHead : SecondarySkipForwardHead) : (backward ? SkipBackArrow : SkipForwardArrow);
            var normal = ((SolidColorBrush)path.Stroke).Color;
            var brush = new SolidColorBrush(normal);
            path.Stroke = brush;
            head.Fill = brush; head.Stroke = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(normal == Brand.Orange ? Colors.WhiteSmoke : Brand.Orange, TimeSpan.FromMilliseconds(110)) { AutoReverse = true });
        }

        // ---------- volume: the player's own 0-100 %, boost from Settings ----------

        private const string MutedGlyph = "M15.5,9.5 L20.5,14.5 M20.5,9.5 L15.5,14.5";
        private const string VolumeGlyphData = "M15,9.2 A4,4 0 0 1 15,14.8 M17.6,6.8 A7.5,7.5 0 0 1 17.6,17.2";

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML sets the slider Value during InitializeComponent, before the
            // ctor assigns _settings: a load-time change must be a no-op.
            if (_settings is null || _player is null) return;
            ApplyPlayerGain();
            RefreshVolumeUi();
            if (_volumeSync) return;
            _settings.LastVolume = (int)Math.Round(VolumeSlider.Value);
        }

        /// <summary>Phone /control/volume 0-100 maps 1:1 onto the player's own slider.</summary>
        public void SetVolumeTied(int volume)
        {
            VolumeSlider.Value = Math.Clamp(volume, 0, 100);
            ShowBarOnce();
        }

        public int SharedVolume => (int)Math.Round(VolumeSlider.Value);
        public bool SharedMute => _playerMute;
        /// <summary>The title the phone's /control/status reads: the movie's title, or the
        /// error text while the error state shows (CP1 late-failure behaviour, kept).</summary>
        public string CurrentMovieTitle => _errorShown ? ErrorMessage.Text : MovieTitle.Text;

        /// <summary>Decoder gain = slider x boost (100 % boost = none, 200 % = VLC-style
        /// doubling). Exposed for tests.</summary>
        internal static int GainFor(double slider, int boostPct) => (int)(Math.Clamp(slider, 0, 100) * boostPct / 100.0);

        // The decoder gain as last APPLIED (libVLC's Volume read-back is -1 with no
        // audio output, e.g. on CI), kept in step with every assignment.
        private int _appliedGain = -1;
        /// <summary>Test seam: the gain last handed to the decoder.</summary>
        internal int AppliedPlayerGain => _appliedGain;

        private void ApplyPlayerGain()
        {
            _appliedGain = GainFor(VolumeSlider.Value, _settings.VolumeBoostMaxPct);
            _player.Volume = _appliedGain;
        }

        private void OnSettingsChanged()
        {
            ApplyBarStyle();
            ApplyPlayerGain(); // Boost (VolumeBoostMaxPct) reaches the playing movie now
            RefreshSubtitleSizeUi();
        }

        private void OnMute(object sender, RoutedEventArgs e) => SetMuteTied(!_playerMute);

        /// <summary>Mute is the player's own: bar button, M key and the phone all land here.</summary>
        public void SetMuteTied(bool mute)
        {
            _playerMute = mute;
            _player.Mute = mute;
            RefreshVolumeUi();
        }

        /// <summary>One place that draws volume state: glyph, "62%" or "Muted",
        /// and a dimmed slider while muted.</summary>
        private void RefreshVolumeUi()
        {
            if (VolumeLabel == null) return;
            VolumeButton.Opacity = 1.0;
            VolumeGlyph.Data = Geometry.Parse(_playerMute ? MutedGlyph : VolumeGlyphData);
            VolumeLabel.Text = _playerMute ? "Muted" : SharedVolume + "%";
            VolumeSlider.Opacity = _playerMute ? 0.45 : 1.0;
        }

        // ---------- pop-ups: one open at a time (item 59) ----------

        private Border[] Popups => new[] { SubtitlesPopup, SpeedPopup, SleepPopup };

        /// <summary>Test/CI seam: which pop-up is open ("subtitles", "speed", "sleep") or null.</summary>
        internal string? OpenPopupName =>
            ReferenceEquals(_openPopup, SubtitlesPopup) ? "subtitles" :
            ReferenceEquals(_openPopup, SpeedPopup) ? "speed" :
            ReferenceEquals(_openPopup, SleepPopup) ? "sleep" : null;

        /// <summary>Open [popup] above [anchor], closing any other; the same button again
        /// closes it. The open pop-up holds the bar up (auto-hide waits).</summary>
        internal void TogglePopup(Border popup, FrameworkElement anchor)
        {
            if (ReferenceEquals(_openPopup, popup)) { ClosePopups(); return; }
            foreach (var p in Popups) if (!ReferenceEquals(p, popup)) p.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(popup, SubtitlesPopup)) BuildSubtitleRows();
            else if (ReferenceEquals(popup, SpeedPopup)) RefreshSpeedUi();
            else RefreshSleepUi();
            _openPopup = popup;
            _menuOpen = true;
            popup.Visibility = Visibility.Visible;
            PositionPopup(popup, anchor);
            RevealBar();
            _idleTimer.Stop();
        }

        internal void ClosePopups()
        {
            foreach (var p in Popups) p.Visibility = Visibility.Collapsed;
            var wasOpen = _openPopup != null;
            _openPopup = null;
            _menuOpen = false;
            if (wasOpen && IsLoaded) { _idleTimer.Stop(); _idleTimer.Start(); }
        }

        /// <summary>Centred above its own button, kept 20 px inside the view; bottom edge
        /// 20 px above the bar's top (130 px above the window bottom when windowed, as
        /// render 04).</summary>
        private void PositionPopup(Border popup, FrameworkElement anchor)
        {
            Overlay.UpdateLayout();
            var w = popup.Width;
            var viewW = Overlay.ActualWidth;
            double left = 20, bottom = 130;
            try
            {
                var c = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), Overlay);
                left = Math.Max(20, Math.Min(c.X - w / 2, viewW - w - 20));
                var barTop = ControlBar.TranslatePoint(new Point(0, 0), Overlay).Y;
                bottom = Math.Max(0, Overlay.ActualHeight - barTop + 20);
            }
            catch (InvalidOperationException) { }
            popup.Margin = new Thickness(left, 0, 0, bottom);
        }

        private bool IsInsideOpenPopup(DependencyObject? d)
        {
            if (_openPopup == null || d == null) return false;
            for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (ReferenceEquals(x, _openPopup)) return true;
            return false;
        }

        private bool IsInsideBar(DependencyObject? d)
        {
            for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (ReferenceEquals(x, ControlBar)) return true;
            return false;
        }

        /// <summary>A click anywhere else (the video, the top overlay) closes the pop-up;
        /// clicks on the bar are left to its buttons (switch / toggle).</summary>
        private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_openPopup == null) return;
            var src = e.OriginalSource as DependencyObject;
            if (IsInsideOpenPopup(src) || IsInsideBar(src)) return;
            ClosePopups();
        }

        // ---------- speed (render 04, item 60) ----------

        private void OnSpeed(object sender, RoutedEventArgs e) => TogglePopup(SpeedPopup, SpeedButton);

        private void OnSpeedChip(object sender, RoutedEventArgs e)
        {
            var i = SpeedChips.Children.IndexOf((UIElement)sender);
            if (i >= 0) SetRate(PlayerBarLogic.SpeedChips[i]);
        }

        private void OnSpeedSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_speedSync || _player is null || _settings is null) return;
            SetRate(e.NewValue);
        }

        /// <summary>Live rate change (libVLC SetRate, no reopen); chips, slider, header and
        /// the bar badge follow.</summary>
        internal void SetRate(double rate)
        {
            _rate = PlayerBarLogic.SnapRate(rate);
            _player.SetRate((float)_rate);
            RefreshSpeedUi();
        }

        /// <summary>Test seams: libVLC's rate read right after the latest media handoff,
        /// and how many handoffs had to set the bar's rate again.</summary>
        internal float RateAtHandoff { get; private set; } = 1f;
        internal int RateReapplyCount { get; private set; }

        /// <summary>The rate last handed to libVLC (its read-back needs a playing input).</summary>
        internal double CurrentRate => _rate;

        private void RefreshSpeedUi()
        {
            if (SpeedLabel == null) return;
            var normal = PlayerBarLogic.IsNormalRate(_rate);
            var label = PlayerBarLogic.FormatRate(_rate);
            SpeedHeaderValue.Text = label;
            var chip = PlayerBarLogic.ChipIndex(_rate);
            for (var i = 0; i < SpeedChips.Children.Count; i++)
                if (SpeedChips.Children[i] is Button b) { b.Tag = i == chip ? "on" : ""; b.Content = PlayerBarLogic.ChipLabel(PlayerBarLogic.SpeedChips[i]); }
            _speedSync = true;
            SpeedSlider.Value = _rate;
            _speedSync = false;
            SpeedLabel.Text = normal ? "" : label;
            SetBadge(SpeedButton, SpeedLabel, !normal);
            var brush = normal ? _iconBrush : Orange;
            SpeedButton.Foreground = brush; ApplyGlyphBrush(SpeedButton, brush);
            SpeedButton.ToolTip = normal ? "Playback speed" : "Playback speed (" + label + ")";
            Reflow();
        }

        /// <summary>A badge widens its 38 px button to padding 0 8 + icon + 5 + text
        /// (render 04 .cb:has(.badge)); at reflow level 4 badges give way, icons stay orange.</summary>
        private void SetBadge(Button button, TextBlock badge, bool on)
        {
            var show = on && _reflowLevel < 4;
            badge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            button.Padding = show ? new Thickness(8, 0, 8, 0) : new Thickness(0);
        }

        // ---------- sleep timer (render 04, items 61-62) ----------

        private void OnSleep(object sender, RoutedEventArgs e) => TogglePopup(SleepPopup, SleepButton);

        private void OnSleepRow(object sender, RoutedEventArgs e)
        {
            var i = SleepRows.Children.IndexOf((UIElement)sender);
            if (i < 0) return;
            SetSleep(SleepTimerState.Choices[i].Minutes);
        }

        /// <summary>Test seam: the clock the sleep timer reads.</summary>
        internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        internal SleepTimerState Sleep => _sleep;
        /// <summary>Test seam: whether the 1-second sleep tick is running.</summary>
        internal bool SleepTickRunning => _sleepTick.IsEnabled;
        /// <summary>Test seam: how many times the sleep timer paused the movie / saw its end.</summary>
        internal int SleepPausedCount { get; private set; }
        internal int MediaEndedCount { get; private set; }

        /// <summary>0 = off, minutes, or SleepTimerState.EndOfMovie.</summary>
        internal void SetSleep(int minutes)
        {
            if (minutes == 0) _sleep.Off(); else _sleep.Set(minutes, Clock());
            // "End of this movie" belongs to the start it was chosen on (see
            // DropEndOfMovieSleep). Timed choices are not bound to a movie.
            _sleepEndGeneration = minutes == SleepTimerState.EndOfMovie ? _playGeneration : -1;
            if (_sleep.IsActive && !_sleep.IsEndOfMovie) _sleepTick.Start(); else _sleepTick.Stop();
            RefreshSleepUi();
        }

        /// <summary>Once a second while a timed choice runs: badge, header, and the pause
        /// when the time is up.</summary>
        internal void SleepTick()
        {
            if (_sleep.IsDue(Clock())) { SleepFire("timer"); return; }
            RefreshSleepUi();
        }

        /// <summary>Time is up: PAUSE and stay in the player (progress saved), as the
        /// render's footnote says. Never stop, never go back to the library (item 62).
        /// For "End of this movie" libVLC has already reached the end (EndReached); the
        /// player stays on the ended movie with the bar up.</summary>
        private void SleepFire(string why)
        {
            _sleepTick.Stop();
            if (_player.State is VLCState.Playing or VLCState.Buffering or VLCState.Opening) _player.SetPause(true);
            SavePlaybackProgress();
            _sleep.Off();
            SleepPausedCount++;
            LastSleepFire = why;
            RefreshSleepUi();
            RevealBar();
            _idleTimer.Stop();
            UpdatePlayPauseGlyph();
            ShowOsd(why == "end" ? "Sleep timer: the movie has ended" : "Sleep timer: paused");
            SleepPaused?.Invoke();
        }
        internal string? LastSleepFire { get; private set; }

        /// <summary>EndReached of the media libVLC holds now, after its start settled
        /// (EndReached before Playing is a failed start, handled by FailNativeStart).</summary>
        private void OnMediaFinished(NativeStart a)
        {
            if (!ReferenceEquals(a, _mediaStart) || a.Waiting) return;
            MediaEndedCount++;
            RevealBar();
            if (_sleep.IsEndOfMovie && _sleepEndGeneration == a.Generation) SleepFire("end");
            UpdatePlayPauseGlyph();
        }

        // The start (play generation) "End of this movie" was chosen on; -1 = none.
        private int _sleepEndGeneration = -1;

        /// <summary>"End of this movie" means THIS movie: once the movie it was chosen on
        /// is no longer the current one (Back, tray close, quit, a failed start, or any new
        /// start - library, phone, Try again), the choice is turned off, so it never fires
        /// at another movie's end. Called wherever the play generation moves on. The
        /// timed choices (15 min ... 1 h 30) are left exactly as they were.</summary>
        private void DropEndOfMovieSleep()
        {
            if (!_sleep.IsEndOfMovie) return;
            _sleep.Off();
            _sleepEndGeneration = -1;
            EndOfMovieDroppedCount++;
            RefreshSleepUi();
        }

        /// <summary>Test seam: how many times "End of this movie" was turned off because its
        /// movie was left.</summary>
        internal int EndOfMovieDroppedCount { get; private set; }

        private void RefreshSleepUi()
        {
            if (SleepLabel == null) return;
            var now = Clock();
            var on = _sleep.IsActive;
            SleepLabel.Text = _sleep.Badge(now);
            SetBadge(SleepButton, SleepLabel, on);
            SleepHeaderValue.Text = _sleep.Header(now);
            for (var i = 0; i < SleepRows.Children.Count; i++)
                if (SleepRows.Children[i] is Button b) b.Tag = SleepTimerState.Choices[i].Minutes == _sleep.Minutes ? "sel" : "";
            var brush = on ? Orange : _iconBrush;
            SleepButton.Foreground = brush; ApplyGlyphBrush(SleepButton, brush);
            SleepButton.ToolTip = on ? "Sleep timer (" + SleepHeaderValue.Text + ")" : "Sleep timer";
            Reflow();
        }

        // ---------- subtitles pop-up (render 04, item 66) ----------

        /// <summary>Real subtitle tracks only: libVLC lists a "Disable" entry with
        /// id -1 first, which is "Off", not a track (P0-3).</summary>
        internal static LibVLCSharp.Shared.Structures.TrackDescription[] RealTracks(LibVLCSharp.Shared.Structures.TrackDescription[]? tracks) =>
            (tracks ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>()).Where(t => t.Id != -1).ToArray();

        private static string TrackLabel(LibVLCSharp.Shared.Structures.TrackDescription t, int index) =>
            string.IsNullOrWhiteSpace(t.Name) ? "Track " + (index + 1) : t.Name;

        private void OnCaptions(object sender, RoutedEventArgs e) => TogglePopup(SubtitlesPopup, CaptionsButton);

        /// <summary>Off, each real track by name (the current one selected), then the
        /// orange "Add subtitle file..." row.</summary>
        private void BuildSubtitleRows()
        {
            SubtitleTrackList.Children.Clear();
            var current = _player.Spu;
            var tracks = RealTracks(_player.SpuDescription);
            var offSelected = current == -1 || tracks.All(t => t.Id != current);
            SubtitleTrackList.Children.Add(MakeTrackRow("Off", -1, offSelected));
            for (var i = 0; i < tracks.Length; i++)
                SubtitleTrackList.Children.Add(MakeTrackRow(TrackLabel(tracks[i], i), tracks[i].Id, tracks[i].Id == current));
            AddSubtitleRow.IsEnabled = _safeSource != null;
            RefreshSubtitleSizeUi();
            SubDelayText.Text = PlayerBarLogic.FormatDelay(_subDelayMs);
        }

        private Button MakeTrackRow(string label, int id, bool selected)
        {
            var row = new Button { Style = (Style)FindResource("ZPopupRow"), Content = label, Tag = selected ? "sel" : "" };
            System.Windows.Automation.AutomationProperties.SetName(row, label);
            row.Click += (_, __) => { SelectSubtitle(id, label); BuildSubtitleRows(); };
            return row;
        }

        /// <summary>The same path the pop-up, the phone and tests take.</summary>
        public void SelectSubtitle(int trackId, string? label = null)
        {
            _player.SetSpu(trackId);
            if (label == null)
            {
                var tracks = RealTracks(_player.SpuDescription);
                var idx = Array.FindIndex(tracks, t => t.Id == trackId);
                label = trackId == -1 || idx < 0 ? "Off" : TrackLabel(tracks[idx], idx);
            }
            ShowOsd("Subtitles: " + label);
            RefreshCaptionsUi(trackId);
        }

        private void OnAddSubtitleRow(object sender, RoutedEventArgs e)
        {
            ClosePopups();
            AddSubtitleFile();
        }

        private void AddSubtitleFile()
        {
            var source = _safeSource;
            if (source == null) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add subtitle file",
                Filter = "Subtitles (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt",
            };
            var owner = Window.GetWindow(this);
            if ((owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) != true) return;
            try
            {
                var staged = source.StageSubtitle(new Uri(dialog.FileName).AbsoluteUri);
                _player.AddSlave(MediaSlaveType.Subtitle, new Uri(staged).AbsoluteUri, true);
                ShowOsd("Subtitles: " + System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Subtitle file refused: " + ex.GetType().Name);
                ShowOsd("That subtitle file could not be used");
            }
        }

        private void OnSubtitleSize(object sender, RoutedEventArgs e)
        {
            var size = (string)((Button)sender).Content;
            _settings.SubtitleSize = size;
            RefreshSubtitleSizeUi();
            var name = size == "S" ? "Small" : size == "L" ? "Large" : "Medium";
            // U4: libVLC 3 has no live text-size change; never re-open the movie for it.
            // Measured (SubtitleSizePathTests): the size is read only when the LibVLC
            // instance is created; per-media options and a new MediaPlayer do not change
            // it. Closing the window keeps Zephyr (and its LibVLC) running in the tray.
            ShowOsd("Subtitle size: " + name + ". It applies after you quit Zephyr Player (tray menu) and open it again.");
        }

        private void RefreshSubtitleSizeUi()
        {
            if (SubSizeM == null || _settings is null) return;
            var size = _settings.SubtitleSize;
            SubSizeS.Tag = size == "S" ? "on" : "";
            SubSizeM.Tag = size == "M" ? "on" : "";
            SubSizeL.Tag = size == "L" ? "on" : "";
        }

        private void OnSubtitleDelayMinus(object sender, RoutedEventArgs e) => SetSubtitleDelay(_subDelayMs - PlayerBarLogic.SubtitleDelayStepMs);
        private void OnSubtitleDelayPlus(object sender, RoutedEventArgs e) => SetSubtitleDelay(_subDelayMs + PlayerBarLogic.SubtitleDelayStepMs);

        /// <summary>Live subtitle delay (libVLC SetSpuDelay, microseconds), 0.1 s steps.</summary>
        internal void SetSubtitleDelay(int ms)
        {
            _subDelayMs = Math.Clamp(ms, -60000, 60000);
            _player.SetSpuDelay(_subDelayMs * 1000L);
            SubDelayText.Text = PlayerBarLogic.FormatDelay(_subDelayMs);
        }
        internal int SubtitleDelayMs => _subDelayMs;

        private bool _autoSelectSidecar;
        /// <summary>One-shot: the first real subtitle track of a local movie's sidecar
        /// is selected when libVLC selected none.</summary>
        private void TryAutoSelectSidecar()
        {
            if (!_autoSelectSidecar) return;
            var tracks = RealTracks(_player.SpuDescription);
            if (tracks.Length == 0) return;
            _autoSelectSidecar = false;
            if (_player.Spu == -1) _player.SetSpu(tracks[0].Id);
            RefreshCaptionsUi();
        }

        /// <summary>Orange subtitles button while a track is on (render 02).</summary>
        private void RefreshCaptionsUi() => RefreshCaptionsUi(_player.Spu);
        private void RefreshCaptionsUi(int spu)
        {
            if (_settings is null) return;
            var on = spu != -1 && RealTracks(_player.SpuDescription).Any(t => t.Id == spu);
            var brush = on ? Orange : _iconBrush;
            CaptionsButton.Foreground = brush;
            ApplyGlyphBrush(CaptionsButton, brush);
            CaptionsButton.Tag = on ? "on" : "off";
            CaptionsButton.ToolTip = on ? "Subtitles (on)" : "Subtitles";
            if (ReferenceEquals(_openPopup, SubtitlesPopup)) BuildSubtitleRows();
        }

        private void ShowOsd(string text)
        {
            OsdText.Text = text;
            Osd.BeginAnimation(OpacityProperty, null);
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
            Osd.BeginAnimation(OpacityProperty, fade);
        }

        // ---------- loading and error states ----------

        private void BeginLoading(string url)
        {
            var title = TitleFor(url);
            DropPendingSettle();
            HideError();
            // Carry-forward (a): the Opening state shows the NEW start - its title, an
            // empty timeline - never the previous movie's time/duration or an old error.
            SetOpening(true);
            ResetTimeline();
            MovieTitle.Text = title;
            LoadingTitle.Text = title;
            LoadingDetail.Text = "Opening...";
            Spinner.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Visible;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever };
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        /// <summary>Opening belongs to the NEW start: until its content shows, an older
        /// movie may still be playing underneath (it is replaced only at handoff), so no
        /// seek of any kind may reach libVLC - it would move the OLD movie while the bar
        /// shows the new title on a zeroed timeline (Instinct CP2 review 1). The seek bar
        /// takes no input (its template has no disabled look), a held drag is dropped,
        /// and every seek path also checks the flag.</summary>
        private void SetOpening(bool on)
        {
            _opening = on;
            if (on) _seeking = false;
            if (SeekBar != null) SeekBar.IsEnabled = !on;
        }

        /// <summary>Test seam: whether the seek bar takes input right now.</summary>
        internal bool SeekBarEnabled => SeekBar.IsEnabled;

        private void EndLoading()
        {
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            LoadingPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>The error state (item 68): message + "Back to library" + "Try again".
        /// It replaces CP1's plain error panel; the bar and top overlay step aside while
        /// it shows (it has its own Back).</summary>
        private void ShowLoadingError(string message)
        {
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            LoadingPanel.Visibility = Visibility.Collapsed;
            ClosePopups();
            _errorShown = true;
            ErrorTitle.Text = LoadingTitle.Text;
            ErrorMessage.Text = message;
            ErrorRetryButton.IsEnabled = _lastStart != null;
            ErrorPanel.Visibility = Visibility.Visible;
            ControlBar.Visibility = Visibility.Collapsed;
            TopOverlay.Visibility = Visibility.Collapsed;
            BottomGradient.Visibility = Visibility.Collapsed;
            // The error panel is this start's content: settled once the overlay has had
            // a few frames to draw it (a fast failure can come before its first frame).
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            settle.Tick += (_, __) => { settle.Stop(); if (IsLoadingErrorShown) RaiseStartSettled(); };
            settle.Start();
        }

        private void HideError()
        {
            _errorShown = false;
            ErrorPanel.Visibility = Visibility.Collapsed;
            ControlBar.Visibility = Visibility.Visible;
            TopOverlay.Visibility = Visibility.Visible;
            BottomGradient.Visibility = _fullscreen ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTryAgain(object sender, RoutedEventArgs e) => _ = RetryLastStartAsync();

        /// <summary>"Try again": the same start again - same URL, position and subtitles.</summary>
        internal async System.Threading.Tasks.Task RetryLastStartAsync()
        {
            if (_lastStart is not { } s) return;
            RetryCount++;
            try { await PlayFromUrlAsync(s.Url, s.Position, s.Subtitles); }
            catch { } // the failure shows as the error state again
        }
        internal int RetryCount { get; private set; }
        internal int StartCount { get; private set; }
        internal (string Url, long? Position, string[] Subtitles)? LastStartArgs => _lastStart;

        /// <summary>True while the loading or the error panel covers the video.</summary>
        public bool IsLoadingShown => LoadingPanel.Visibility == Visibility.Visible || ErrorPanel.Visibility == Visibility.Visible;
        /// <summary>Test seams: the detail line (the error message while the error state
        /// shows), and whether the error state shows.</summary>
        internal string LoadingDetailText => _errorShown ? ErrorMessage.Text : LoadingDetail.Text;
        internal bool IsLoadingErrorShown => ErrorPanel.Visibility == Visibility.Visible;
        internal bool IsOpening => _opening;

        // ---------- bar show / hide (item 65) ----------

        /// <summary>The real pointer is over the bar's card. Read from the cursor position,
        /// not IsMouseOver: the bar lives in LibVLCSharp's overlay window, and a pointer
        /// that jumps from a bar button straight onto another window can leave
        /// IsMouseOver set (CI run 41047779027: the bar never hid after the pop-up clicks).</summary>
        private bool PointerOnBar()
        {
            if (ControlBar.Visibility != Visibility.Visible || PresentationSource.FromVisual(ControlBar) == null) return false;
            if (!GetCursorPos(out var p)) return ControlBar.IsMouseOver;
            try
            {
                var local = ControlBar.PointFromScreen(new Point(p.X, p.Y));
                return local.X >= 0 && local.Y >= 0 && local.X <= ControlBar.ActualWidth && local.Y <= ControlBar.ActualHeight;
            }
            catch (InvalidOperationException) { return false; }
        }

        private bool BarMayHide =>
            !(PointerOnBar() || _seeking || _menuOpen || _errorShown
              || _player.State is VLCState.Paused or VLCState.Ended or VLCState.Stopped or VLCState.NothingSpecial);

        private void RevealBar()
        {
            ControlBar.BeginAnimation(OpacityProperty, null); ControlBar.Opacity = 1;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
            BottomGradient.BeginAnimation(OpacityProperty, null); BottomGradient.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
        }

        private void FadeBar(double to)
        {
            var d = TimeSpan.FromMilliseconds(250);
            ControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(to, d));
            TopOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(to, d));
            BottomGradient.BeginAnimation(OpacityProperty, new DoubleAnimation(to, d));
        }

        /// <summary>Phone-initiated pause/resume shows the bar once (review blocker 3).</summary>
        public void ShowBarOnce()
        {
            RevealBar();
            _idleTimer.Stop();
            if (IsLoaded) _idleTimer.Start();
        }

        /// <summary>Test seam: run the idle check now (as its 3 s timer would).</summary>
        internal void IdleTickForTest() { if (BarMayHide) { ControlBar.BeginAnimation(OpacityProperty, null); ControlBar.Opacity = 0; TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 0; } }

        public bool IsFullscreen => _fullscreen;

        /// <summary>Raised when the view enters/leaves full screen; the shell owns the
        /// window chrome and applies it.</summary>
        public event Action<bool>? FullscreenChanged;

        /// <summary>A movie actually in progress (playing or paused) - tray/relaunch
        /// restore surfaces the player view instead of the library. A stopped or ended player
        /// restores Home, not a dead black frame.</summary>
        public bool IsPlayingMedia => _player.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening;

        private const string EnterFullscreenGlyph = "M4,9 V5.5 A1.5,1.5 0 0 1 5.5,4 H9 M15,4 H18.5 A1.5,1.5 0 0 1 20,5.5 V9 M20,15 V18.5 A1.5,1.5 0 0 1 18.5,20 H15 M9,20 H5.5 A1.5,1.5 0 0 1 4,18.5 V15";
        private const string ExitFullscreenGlyph = "M9,4 V7.5 A1.5,1.5 0 0 1 7.5,9 H4 M15,4 V7.5 A1.5,1.5 0 0 0 16.5,9 H20 M20,15 H16.5 A1.5,1.5 0 0 0 15,16.5 V20 M4,15 H7.5 A1.5,1.5 0 0 1 9,16.5 V20";

        public void SetFullscreen(bool on)
        {
            if (on == _fullscreen) return;
            _fullscreen = on;
            ClosePopups();
            ApplyLayoutMode();
            FullscreenChanged?.Invoke(on);
            FullscreenGlyph.Data = Geometry.Parse(on ? ExitFullscreenGlyph : EnterFullscreenGlyph);
            FullscreenButton.ToolTip = on ? "Exit full screen (Esc)" : "Full screen (F)";
            // The bar must be reachable right after the chrome switch (reported
            // missing-bar bug): show it once, then the idle timer owns hiding.
            ShowBarOnce();
        }

        private void OnFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!IsFullscreen);

        private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_fullscreen) ApplyPillSize();
            if (_openPopup != null) ClosePopups();
        }

        /// <summary>Windowed (render 02) or fullscreen (render 03) metrics for the top
        /// overlay, the bar and the centre cluster.</summary>
        private void ApplyLayoutMode()
        {
            var fs = _fullscreen;
            // Top overlay: 120 px rgba(0,0,0,.7) + meta, or 140 px rgba(0,0,0,.62), back at
            // 24/22 on rgba(20,20,24,.55) with a 1 px rgba(255,255,255,.08) edge, title shadow.
            TopOverlay.Height = fs ? 140 : 120;
            TopOverlay.Padding = fs ? new Thickness(24, 22, 24, 0) : new Thickness(20, 16, 20, 0);
            TopGradientStart.Color = fs ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xB3, 0, 0, 0);
            HomeButton.Tag = new SolidColorBrush(fs ? Color.FromArgb(0x8C, 20, 20, 24) : Color.FromArgb(0x14, 255, 255, 255));
            HomeButton.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
            HomeButton.BorderThickness = new Thickness(fs ? 1 : 0);
            TitleShadow.Opacity = fs ? 0.6 : 0;
            MovieMeta.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
            BottomGradient.Visibility = fs && !_errorShown ? Visibility.Visible : Visibility.Collapsed;
            // Centre cluster: 44/46/50 px, gap 14, Play margin 0 8 (windowed) or 40/42/46,
            // gap 12, margin 0 6 (the pill's 1000 px layout).
            double sec = fs ? 40 : 44, pri = fs ? 42 : 46, play = fs ? 46 : 50, gap = fs ? 12 : 14, pm = fs ? 6 : 8;
            SecondarySkipBackButton.Width = SecondarySkipBackButton.Height = sec;
            SecondarySkipForwardButton.Width = SecondarySkipForwardButton.Height = sec;
            SecondaryBackBox.Width = SecondaryBackBox.Height = sec - 4;
            SecondaryForwardBox.Width = SecondaryForwardBox.Height = sec - 4;
            SkipBackButton.Width = SkipBackButton.Height = pri;
            SkipForwardButton.Width = SkipForwardButton.Height = pri;
            SkipBackBox.Width = SkipBackBox.Height = pri - 4;
            SkipForwardBox.Width = SkipForwardBox.Height = pri - 4;
            PlayPauseButton.Width = PlayPauseButton.Height = play;
            PlayBox.Width = PlayBox.Height = play;
            SkipBackButton.Margin = new Thickness(gap, 0, 0, 0);
            SkipForwardButton.Margin = new Thickness(0, 0, gap, 0);
            PlayPauseButton.Margin = new Thickness(gap + pm, 0, gap + pm, 0);
            var k = (sec - 4) / 40.0;
            SecondarySkipBackLabel.Margin = SecondarySkipForwardLabel.Margin = new Thickness(0, 18 * k, 0, 0);
            ApplySkipLabels();
            if (fs)
            {
                ControlBar.HorizontalAlignment = HorizontalAlignment.Center;
                ControlBar.CornerRadius = new CornerRadius(22);
                ControlBar.Padding = new Thickness(16, 7, 16, 5);
                ControlBar.Margin = new Thickness(0, 0, 0, PlayerBarLogic.PillBottom);
                ControlBar.LayoutTransform = new ScaleTransform(PlayerBarLogic.PillScale, PlayerBarLogic.PillScale);
                ApplyPillSize();
            }
            else
            {
                ControlBar.LayoutTransform = Transform.Identity;
                ControlBar.Width = double.NaN;
                ControlBar.MaxWidth = double.PositiveInfinity;
                ControlBar.HorizontalAlignment = HorizontalAlignment.Stretch;
                ControlBar.CornerRadius = new CornerRadius(16);
                ControlBar.Padding = new Thickness(18, 10, 18, 9);
                ControlBar.Margin = new Thickness(20);
            }
            Reflow();
        }

        /// <summary>The pill: laid out at up to 1000 px, drawn at 0.78 (at most 780 px),
        /// clamped to the screen with a 24 px gap each side (Instinct U2).</summary>
        private void ApplyPillSize()
        {
            var viewport = Overlay.ActualWidth > 0 ? Overlay.ActualWidth : ActualWidth;
            if (viewport <= 0) viewport = SystemParameters.PrimaryScreenWidth;
            ControlBar.Width = PlayerBarLogic.PillLayoutWidth(viewport);
            ControlBar.MaxWidth = PlayerBarLogic.PillLayoutMax;
        }

        /// <summary>Test/CI seam: the pill's drawn width for the current viewport.</summary>
        internal double PillDrawnWidth => _fullscreen ? ControlBar.Width * PlayerBarLogic.PillScale : 0;

        private void OnBarSizeChanged(object sender, SizeChangedEventArgs e) => Reflow();

        internal int ReflowLevelNow => _reflowLevel;

        /// <summary>At narrow widths the bar gives way in steps (no % label, a shorter
        /// volume track, speaker only, no badges) so the side groups never run into the
        /// centre cluster and Play stays on the centre line.</summary>
        private void Reflow()
        {
            if (ControlBar == null || ControlBar.ActualWidth <= 0) return;
            var inner = ControlBar.ActualWidth - ControlBar.Padding.Left - ControlBar.Padding.Right - 2;
            CenterPlaybackControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var center = CenterPlaybackControls.DesiredSize.Width;
            // A badge button is 8 + icon 22 + 5 + text + 8 wide against 38 without: text + 5 more.
            double BadgeWidth(TextBlock t) { if (string.IsNullOrEmpty(t.Text)) return 0; t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return t.DesiredSize.Width + 5; }
            var speedBadge = PlayerBarLogic.IsNormalRate(_rate) ? 0 : BadgeWidth(SpeedLabel);
            var sleepBadge = _sleep.IsActive ? BadgeWidth(SleepLabel) : 0;
            var shown = new[] { CaptionsButton, SpeedButton, SleepButton, FullscreenButton }.Count(b => b.Visibility == Visibility.Visible);
            var rightNoBadges = shown * 38 + Math.Max(0, shown - 1) * 4;
            var right = rightNoBadges + (SpeedButton.Visibility == Visibility.Visible ? Math.Max(0, speedBadge) : 0) + (SleepButton.Visibility == Visibility.Visible ? Math.Max(0, sleepBadge) : 0);
            var level = PlayerBarLogic.ReflowLevel(inner, center, right, rightNoBadges);
            _reflowLevel = level;
            VolumeLabel.Visibility = level >= 1 ? Visibility.Collapsed : Visibility.Visible;
            VolumeSlider.Width = level >= 2 ? 56 : 92;
            VolumeSlider.Visibility = level >= 3 ? Visibility.Collapsed : Visibility.Visible;
            SetBadge(SpeedButton, SpeedLabel, !PlayerBarLogic.IsNormalRate(_rate));
            SetBadge(SleepButton, SleepLabel, _sleep.IsActive);
        }

        // ---------- bar styling (Settings, live; item 58) ----------

        private Brush _iconBrush = IconDefault;

        private static void ApplyGlyphBrush(DependencyObject element, Brush brush)
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
        private static void SetSkipLabel(TextBlock tb, int seconds, double size)
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

        private void ApplySkipLabels()
        {
            var pri = (SkipBackBox.Width) / 42.0 * 12.6;
            var sec = (SecondaryBackBox.Width) / 40.0 * 13.0;
            SetSkipLabel(SkipBackLabel, _settings.PrimarySkipSeconds, pri);
            SetSkipLabel(SkipForwardLabel, _settings.PrimarySkipSeconds, pri);
            SetSkipLabel(SecondarySkipBackLabel, _settings.SecondarySkipSeconds, sec);
            SetSkipLabel(SecondarySkipForwardLabel, _settings.SecondarySkipSeconds, sec);
        }

        /// <summary>The five colour sets (render 06b; "Default is the original") and
        /// Outline/Solid. Original = render 02: white primary rings and labels, orange
        /// secondary arrows and labels, orange Play. Orange / White = everything in that
        /// colour. "White, orange rings" = orange rings, white labels. "Orange, white rings"
        /// = white rings, orange labels.</summary>
        public void ApplyBarStyle()
        {
            _secondaryBackHighlight.Stop();_secondaryForwardHighlight.Stop();
            var style = _settings.BarColorStyle;
            Brush priRing, priLabel, secRing, secLabel, play, icons;
            switch (style)
            {
                case "orange": priRing = priLabel = secRing = secLabel = play = Orange; icons = Orange; break;
                case "white": priRing = priLabel = secRing = secLabel = play = RingDefault; icons = IconDefault; break;
                case "twotone": priRing = secRing = Orange; priLabel = secLabel = RingDefault; play = Orange; icons = IconDefault; break;
                case "inverse": priRing = secRing = RingDefault; priLabel = secLabel = Orange; play = Orange; icons = Orange; break;
                default: priRing = priLabel = RingDefault; secRing = secLabel = Orange; play = Orange; icons = IconDefault; break;
            }
            _iconBrush = icons;

            SkipBackRing.Stroke = priRing; SkipForwardRing.Stroke = priRing;
            SkipBackArrow.Fill = SkipBackArrow.Stroke = priRing;
            SkipForwardArrow.Fill = SkipForwardArrow.Stroke = priRing;
            SkipBackLabel.Foreground = priLabel; SkipForwardLabel.Foreground = priLabel;
            SecondarySkipBackIcon.Stroke = secRing; SecondarySkipForwardIcon.Stroke = secRing;
            SecondarySkipBackHead.Fill = SecondarySkipBackHead.Stroke = secRing;
            SecondarySkipForwardHead.Fill = SecondarySkipForwardHead.Stroke = secRing;
            SecondarySkipBackLabel.Foreground = secLabel; SecondarySkipForwardLabel.Foreground = secLabel;
            ApplySkipLabels();
            SecondarySkipBackButton.ToolTip = $"Back {_settings.SecondarySkipSeconds} s";
            SecondarySkipForwardButton.ToolTip = $"Forward {_settings.SecondarySkipSeconds} s";

            // Outline (render 02): ring r 10.4/24, 1.25 stroke, 10 % fill, coloured glyph.
            // Solid: a filled disc with a dark glyph.
            var hollow = _settings.IconStyle == "hollow";
            var playColor = ((SolidColorBrush)play).Color;
            PlayRing.Stroke = hollow ? play : Brushes.Transparent;
            PlayRing.Fill = hollow ? (playColor == Brand.Orange ? OrangeTint : WhiteTint) : play;
            PlayGlyph.Fill = PlayGlyph.Stroke = hollow ? play : Dark;
            foreach (var child in PauseGlyph.Children)
                if (child is System.Windows.Shapes.Rectangle r) r.Fill = hollow ? play : Dark;

            VolumeButton.Foreground = icons; ApplyGlyphBrush(VolumeButton, icons);
            FullscreenButton.Foreground = icons; ApplyGlyphBrush(FullscreenButton, icons);

            SpeedButton.Visibility = _settings.ShowSpeed ? Visibility.Visible : Visibility.Collapsed;
            SleepButton.Visibility = _settings.ShowSleepTimer ? Visibility.Visible : Visibility.Collapsed;
            RefreshCaptionsUi();
            RefreshSpeedUi();
            RefreshSleepUi();
            Reflow();
        }

        // ---------- CI readback (status JSON) ----------

        /// <summary>What the bar shows right now, for the capture build's /control/status
        /// (CI_CAPTURE_HOOK only) and tests.</summary>
        internal System.Collections.Generic.Dictionary<string, object?> UiState()
        {
            Rect BarRect()
            {
                try
                {
                    if (PresentationSource.FromVisual(ControlBar) == null || ControlBar.ActualWidth <= 0) return Rect.Empty;
                    var tl = ControlBar.PointToScreen(new Point(0, 0));
                    var br = ControlBar.PointToScreen(new Point(ControlBar.ActualWidth, ControlBar.ActualHeight));
                    return new Rect(tl, br);
                }
                catch { return Rect.Empty; }
            }
            var bar = BarRect();
            return new System.Collections.Generic.Dictionary<string, object?>
            {
                ["bar_opacity"] = Math.Round(ControlBar.Opacity, 2),
                ["bar_visible"] = ControlBar.Visibility == Visibility.Visible,
                ["top_opacity"] = Math.Round(TopOverlay.Opacity, 2),
                ["bar_screen"] = bar.IsEmpty ? null : new[] { (int)bar.Left, (int)bar.Top, (int)bar.Right, (int)bar.Bottom },
                ["popup"] = OpenPopupName,
                ["elapsed_text"] = ElapsedText.Text,
                ["remaining_text"] = RemainingText.Text,
                ["total_text"] = TotalText.Text,
                ["title_text"] = MovieTitle.Text,
                ["meta_text"] = MovieMeta.Text,
                ["seek_value"] = Math.Round(SeekBar.Value, 1),
                ["opening"] = _opening,
                ["loading"] = LoadingPanel.Visibility == Visibility.Visible,
                ["error"] = _errorShown,
                ["error_message"] = _errorShown ? ErrorMessage.Text : null,
                ["start_count"] = StartCount,
                ["retry_count"] = RetryCount,
                ["last_start_url"] = _lastStart?.Url,
                ["rate"] = _rate,
                ["speed_badge"] = SpeedLabel.Visibility == Visibility.Visible ? SpeedLabel.Text : null,
                ["sleep_minutes"] = _sleep.Minutes,
                ["sleep_badge"] = SleepLabel.Visibility == Visibility.Visible ? SleepLabel.Text : null,
                ["sleep_paused_count"] = SleepPausedCount,
                ["subtitle_delay_ms"] = _subDelayMs,
                ["subtitle_size"] = _settings.SubtitleSize,
                ["fullscreen"] = _fullscreen,
                ["pill_drawn_width"] = Math.Round(PillDrawnWidth, 1),
                ["reflow"] = _reflowLevel,
                ["last_seek_fraction"] = Math.Round(LastSeekFraction, 4),
            };
        }
    }
}
