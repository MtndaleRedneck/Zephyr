using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
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
        private static readonly SolidColorBrush Orange = new(Brand.Orange);
        private static readonly SolidColorBrush Gold = new(Color.FromRgb(0xF0, 0xC2, 0x3E));
        private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x96, 0x96, 0xA0));
        private static readonly SolidColorBrush Dark = new(Color.FromRgb(0x1B, 0x1B, 0x1E));

        // Brushes are shared app-wide; Freeze makes them safe to touch from any
        // thread (each test case runs on its own STA thread - unfrozen statics
        // threw cross-thread Freezable exceptions, CI-observed).
        static PlayerView()
        {
            RingDefault.Freeze();
            Orange.Freeze();
            Gold.Freeze();
            Muted.Freeze();
            Dark.Freeze();
        }

        private readonly LibVLC _libvlc;
        private readonly MediaPlayer _player;
        private SafeMediaSource? _safeSource;
        private readonly SecondarySkipHighlight _secondaryBackHighlight=new(){Width=54,Height=46,Backward=true,IsHitTestVisible=false};
        private readonly SecondarySkipHighlight _secondaryForwardHighlight=new(){Width=54,Height=46,IsHitTestVisible=false};
        private readonly PlayerSettings _settings;
        private readonly DispatcherTimer _idleTimer = new();
        private readonly DispatcherTimer _clock = new();

        private DispatcherTimer? _sleepTimer;
        private readonly DispatcherTimer _cursorWatch = new() { Interval = TimeSpan.FromMilliseconds(150) };
        [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
        private CursorPoint? _lastScreenCursor;
        private bool _fullscreen;
        private Window? _overlayWindow;
        private bool _menuOpen;

        private bool _seeking;
        private bool _volumeSync;
        private readonly PlaybackHistory _history = new();
        private string _currentSource = "";
        private DateTime _lastHistorySave;
        internal void SavePlaybackProgress()
        {
            if (_player.Length > 0 && _player.Time >= 0) _history.Save(_currentSource, _player.Time, _player.Length);
        }

        /// <summary>Fired when the sleep timer ends playback; the shell lands on the library.</summary>
        public event Action? SleepFired;
        public event Action? HomeRequested;
        private void OnHome(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

        /// <summary>[volume] is accepted for call-site compatibility only: 2.0 volume is
        /// the player's own (owner decision 2) and never reads or writes Windows'
        /// master volume.</summary>
        public PlayerView(LibVLC libvlc, MediaPlayer player, PlayerSettings? settings = null, VolumeService? volume = null)
        {
            InitializeComponent();
            ((Canvas)SecondarySkipBackIcon.Parent).Children.Add(_secondaryBackHighlight);
            ((Canvas)SecondarySkipForwardIcon.Parent).Children.Add(_secondaryForwardHighlight);
            _libvlc = libvlc;
            _player = player;
            _settings = settings ?? new PlayerSettings();
            Loaded += (_, __) => { Video.MediaPlayer = _player; _cursorWatch.Start(); ShowBarOnce(); HookOverlayKeys(); };
            // Unloaded = the shell swapped this view out (library, tray close). Stop
            // every timer; LibVLCSharp hides its overlay window on the same event.
            Unloaded += (_, __) => StopTimers();
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
                // VideoView hosts controls in a separate floating window. Never
                // hide the seek thumb while the pointer is using the bar. And a
                // PAUSED movie keeps the bar (review blocker 3): hiding it while
                // paused strands phone-only viewers with no visible controls.
                if (ControlBar.IsMouseOver || _seeking || _menuOpen || _player.State == LibVLCSharp.Shared.VLCState.Paused) return;
                ControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
                TopOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
                if (IsFullscreen) Overlay.Cursor = Cursors.None;
                _idleTimer.Stop();
            };
            _player.Paused += (_, __) => Dispatcher.BeginInvoke(() =>
            {
                // Phone-pause reveals the bar (review evidence): hiding it while
                // paused strands phone-only viewers with no visible controls.
                ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
                _idleTimer.Stop(); // never auto-hide while paused
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
            DetachNativeStart();
            EndLoading();
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
            LastStartFailure = ex is TimeoutException ? "timeout" : "preparation";
            var message = ex is TimeoutException ? ex.Message : OpenFailedMessage;
            MovieTitle.Text=message;
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
            /// buffering. Loading ends at the first sign of content (see OnNativeContent).</summary>
            internal bool PlayingSeen;
            internal bool Buffered;
            internal string? CompletedBy;
            internal EventHandler<EventArgs>? OnPlaying, OnError, OnEnded, OnOpening;
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
        }

        /// <summary>This start's own Playing. libVLC 3 says Playing when its demuxer has
        /// opened, before the first picture: a slow phone can still be buffering for a
        /// while (CI run 41033075025: about 0.7 s of an all-black video area between
        /// Playing and the first frame on a dribbling source). So Playing alone does not
        /// end loading; the first sign of content does (OnNativeContent). A start that
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

        /// <summary>Content of this start is on its way to the screen: after Playing,
        /// libVLC finished buffering (Buffering 100 %, when its decoders are released to
        /// show pictures) or its clock is past 0 (CheckNativeStall). Loading ends here,
        /// for the newest start only.</summary>
        private void OnNativeContent(NativeStart a, string what)
        {
            if (!a.Waiting || !a.PlayingSeen || !ReferenceEquals(a, _nativeStart)) return;
            _stallWatch.Stop();
            DetachNativeHandlers(a);
            a.CompletedBy = what;
            if (a.Generation != _playGeneration) return;
            LastStartFailure = null;
            EndLoading();
            StartSettled?.Invoke();
        }

        /// <summary>The newest start shows its own content now: its first Playing, or
        /// the error panel. The shell releases the library it kept drawn underneath.</summary>
        public event Action? StartSettled;

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
            MovieTitle.Text=NativeFailedMessage;
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
            await Dispatcher.InvokeAsync(()=>CompletePreparedPlayback(source,generation,timeout.IsCancellationRequested,url,positionSeconds));
        }
        private void CompletePreparedPlayback(SafeMediaSource source,int generation,bool canceled,string url,long? positionSeconds)
        {
            if(generation!=_playGeneration) {source.Dispose();throw new OperationCanceledException("Playback preparation was superseded or stopped.");}
            if(canceled) {source.Dispose();throw new TimeoutException("Movie preparation timed out. Check the phone connection.");}
            SavePlaybackProgress();
            _currentSource = url;
            var localTitle = PlaybackHistory.LocalPath(url);
            MovieTitle.Text = localTitle != null ? System.IO.Path.GetFileNameWithoutExtension(localTitle) : "Playing from phone";
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
            // The replaced source tears down off the UI thread (see StopPlaybackSource).
            if (replaced != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => DisposeReplacedSource(replaced));
            if (positionSeconds.HasValue && positionSeconds.Value > 0)
                _player.Time = positionSeconds.Value * 1000;
            _clock.Start();
            // Phone-started playback shows the bar once (review blocker 3): the PC
            // mouse may never move, so the idle timer alone can't reveal it.
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
            _idleTimer.Stop();
            _idleTimer.Start();
            UpdatePlayPauseGlyph();
        }

        // The player's own mute as last APPLIED. libvlc's Mute read-back is
        // unreliable with no media loaded or no audio device (it returns false
        // regardless of what was set), so this field is the truth the UI and the
        // phone read. Every assignment below keeps both in step.
        private bool _playerMute;
        /// <summary>Test seam (InternalsVisibleTo): the tracked player mute.</summary>
        internal bool TrackedPlayerMute { get => _playerMute; set => _playerMute = value; }

        private void UpdatePosition()
        {
            if (DateTime.UtcNow - _lastHistorySave >= TimeSpan.FromSeconds(5))
            { SavePlaybackProgress(); _lastHistorySave = DateTime.UtcNow; }
            // 2.0: no Windows master poll. Volume and mute are the player's own.
            if (!_seeking)
            {
                var length = _player.Length;
                if (length > 0)
                {
                    var time = _player.Time;
                    SeekBar.Value = 1000.0 * time / length;
                    ElapsedText.Text = Format(time);
                    TotalText.Text = Format(length) + " total";
                    RemainingText.Text = "-" + Format(Math.Max(0, length - time));
                }
            }
            UpdatePlayPauseGlyph();
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
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
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
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || _menuOpen) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) != 0) return;
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
        }

        /// <summary>Belt and braces for every leave/hide path: hide LibVLCSharp's
        /// overlay window right now (Unloaded also hides it, a moment later) and
        /// stop the timers. Win32 never hides an owned window with its owner.</summary>
        public void HideOverlayWindow()
        {
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
            StopPlaybackSource();
        }

        private void OnVideoDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!ControlBar.IsMouseOver) { SetFullscreen(!IsFullscreen); e.Handled = true; }
        }

        private void OnSeekDown(object sender, RoutedEventArgs e) => _seeking = true;

        private void OnSeekUp(object sender, RoutedEventArgs e)
        {
            _seeking = false;
            if (_player.Length > 0)
                _player.Position = (float)(SeekBar.Value / 1000.0);
        }

        // ---------- play / pause / skips ----------

        private void OnPlayPause(object sender, RoutedEventArgs e)
        {
            if (_player.IsPlaying) _player.SetPause(true);
            else if (_player.State == VLCState.Paused) _player.SetPause(false);
            UpdatePlayPauseGlyph();
        }

        private void OnSkipBack(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(false, true), SkipBackSpin, true);

        private void OnSkipForward(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(false, false), SkipForwardSpin, false);

        private void OnSecondarySkipBack(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(true, true), SecondaryBackSpin, true);

        private void OnSecondarySkipForward(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(true, false), SecondaryForwardSpin, false);

        private void Skip(int deltaSeconds, RotateTransform? spin, bool backward)
        {
            // Nothing loaded = nothing to skip: no time change, no spin, no flash.
            if (_player.Length <= 0) return;
            _player.Time = Math.Clamp(_player.Time + deltaSeconds * 1000L, 0, _player.Length);
            // Rotate only the ring/arc; the duration label stays upright.
            var secondary=spin==SecondaryBackSpin||spin==SecondaryForwardSpin;
            if (spin != null && !secondary)
            {
                spin.BeginAnimation(RotateTransform.AngleProperty, null);
                spin.Angle = 0;
                var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), FillBehavior = FillBehavior.Stop };
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                anim.KeyFrames.Add(new SplineDoubleKeyFrame(backward ? -360 : 360, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new KeySpline(.4, 0, .2, 1)));
                spin.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
            if(secondary)
            {
                var highlight=backward?_secondaryBackHighlight:_secondaryForwardHighlight;
                if(_settings.ButtonFlash)highlight.Start(((SolidColorBrush)(backward?SecondarySkipBackIcon:SecondarySkipForwardIcon).Stroke).Color);else highlight.Stop();
            }
            else if (_settings.ButtonFlash) Flash(backward,false);
        }

        private void Flash(bool backward, bool secondary)
        {
            // Animate only the pressed glyph. The number keeps its selected color.
            var path = secondary ? (backward ? SecondarySkipBackIcon : SecondarySkipForwardIcon) : (backward ? SkipBackRing : SkipForwardRing);
            var arrow = backward ? SkipBackArrow : SkipForwardArrow;
            var normal = ((SolidColorBrush)path.Stroke).Color;
            var brush = new SolidColorBrush(normal);
            path.Stroke = brush;
            if (!secondary) arrow.Fill = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(normal == Brand.Orange ? Colors.WhiteSmoke : Brand.Orange, TimeSpan.FromMilliseconds(110)) { AutoReverse = true });
        }

        // ---------- volume: the player's own 0-100 %, boost from Settings ----------

        private const string MutedGlyph = "M2,9 H6 L11,5 V21 L6,17 H2 Z M15,9 L23,17 M23,9 L15,17";
        private const string VolumeGlyphData = "M2,10 v6 h4 l5,4 V6 l-5,4 Z M14,9 a4,4 0 0 1 0,8";

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
        public string CurrentMovieTitle => MovieTitle.Text;

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

        // ---------- speed + sleep timer (bar toggles, off by default) ----------

        private void OnSpeed(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            foreach (var rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                var item = new MenuItem { Header = rate + "x", IsChecked = Math.Abs(_player.Rate - rate) < 0.01 };
                var r = (float)rate;
                item.Click += (_, __) => { _player.SetRate(r); SpeedLabel.Text = rate + "x"; };
                menu.Items.Add(item);
            }
            _menuOpen = true;
            menu.Closed += (_, __) => { _menuOpen = false; ShowBarOnce(); };
            menu.IsOpen = true;
        }

        private void OnSleep(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var off = new MenuItem { Header = "Off", IsChecked = _sleepTimer == null };
            off.Click += (_, __) => { _sleepTimer?.Stop(); _sleepTimer = null; SleepLabel.Text = "zZ"; };
            menu.Items.Add(off);
            foreach (var min in new[] { 15, 30, 45, 60 })
            {
                var item = new MenuItem { Header = min + " min" };
                item.Click += (_, __) =>
                {
                    _sleepTimer?.Stop();
                    _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(min) };
                    _sleepTimer.Tick += (_, ___) =>
                    {
                        _sleepTimer?.Stop();
                        _sleepTimer = null;
                        SleepLabel.Text = "zZ";
                        SavePlaybackProgress();
                        StopPlaybackSource();
                        SleepFired?.Invoke();
                    };
                    _sleepTimer.Start();
                    SleepLabel.Text = min + "m";
                };
                menu.Items.Add(item);
            }
            _menuOpen = true;
            menu.Closed += (_, __) => { _menuOpen = false; ShowBarOnce(); };
            menu.IsOpen = true;
        }

        // ---------- captions / fullscreen ----------

        /// <summary>Real subtitle tracks only: libVLC lists a "Disable" entry with
        /// id -1 first, which is "Off", not a track (P0-3).</summary>
        internal static LibVLCSharp.Shared.Structures.TrackDescription[] RealTracks(LibVLCSharp.Shared.Structures.TrackDescription[]? tracks) =>
            (tracks ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>()).Where(t => t.Id != -1).ToArray();

        private static string TrackLabel(LibVLCSharp.Shared.Structures.TrackDescription t, int index) =>
            string.IsNullOrWhiteSpace(t.Name) ? "Track " + (index + 1) : t.Name;

        /// <summary>Subtitles pop-up (P0-3): Off, each real track by name, then
        /// "Add subtitle file...". The current choice is checked.</summary>
        private void OnCaptions(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = CaptionsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            menu.Items.Add(new MenuItem { Header = "Subtitles", IsEnabled = false, Opacity = 0.7 });
            var current = _player.Spu;
            var tracks = RealTracks(_player.SpuDescription);
            var off = new MenuItem { Header = "Off", IsChecked = current == -1 || tracks.All(t => t.Id != current) };
            off.Click += (_, __) => SelectSubtitle(-1, "Off");
            menu.Items.Add(off);
            for (var i = 0; i < tracks.Length; i++)
            {
                var track = tracks[i];
                var label = TrackLabel(track, i);
                var item = new MenuItem { Header = label, IsChecked = track.Id == current };
                item.Click += (_, __) => SelectSubtitle(track.Id, label);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = "Add subtitle file...", IsEnabled = _safeSource != null };
            add.Click += (_, __) => AddSubtitleFile();
            menu.Items.Add(add);
            _menuOpen = true;
            menu.Closed += (_, __) => { _menuOpen = false; ShowBarOnce(); };
            menu.IsOpen = true;
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
            var style = _settings.BarColorStyle;
            Brush normal = style is "orange" or "inverse" ? Orange : RingDefault;
            var brush = on ? Orange : normal;
            CaptionsButton.Foreground = brush;
            ApplyGlyphBrush(CaptionsButton, brush);
            CaptionsButton.Tag = on ? "on" : "off";
            CaptionsButton.ToolTip = on ? "Subtitles (on)" : "Subtitles";
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

        // ---------- loading state ----------

        private void BeginLoading(string url)
        {
            var local = PlaybackHistory.LocalPath(url);
            LoadingTitle.Text = local != null ? System.IO.Path.GetFileNameWithoutExtension(local) : "Movie from your phone";
            LoadingDetail.Text = "Opening...";
            LoadingBackButton.Visibility = Visibility.Collapsed;
            Spinner.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Visible;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever };
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        private void EndLoading()
        {
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            LoadingPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowLoadingError(string message)
        {
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            Spinner.Visibility = Visibility.Collapsed;
            LoadingDetail.Text = message;
            LoadingBackButton.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Visible;
            // The error panel is this start's content: settled once the overlay has had
            // a few frames to draw it (a fast failure can come before its first frame).
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            settle.Tick += (_, __) => { settle.Stop(); if (IsLoadingErrorShown) StartSettled?.Invoke(); };
            settle.Start();
        }

        /// <summary>True while the loading panel covers the video.</summary>
        public bool IsLoadingShown => LoadingPanel.Visibility == Visibility.Visible;
        /// <summary>Test seams: the loading panel's detail line, and whether it shows
        /// the error surface (spinner gone, Back button shown).</summary>
        internal string LoadingDetailText => LoadingDetail.Text;
        internal bool IsLoadingErrorShown => IsLoadingShown && Spinner.Visibility != Visibility.Visible && LoadingBackButton.Visibility == Visibility.Visible;

        /// <summary>Phone-initiated pause/resume shows the bar once (review blocker 3).</summary>
        public void ShowBarOnce()
        {
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
            _idleTimer.Stop();
            if (IsLoaded) _idleTimer.Start();
        }

        public bool IsFullscreen => _fullscreen;

        /// <summary>Raised when the view enters/leaves full screen; the shell owns the
        /// window chrome and applies it.</summary>
        public event Action<bool>? FullscreenChanged;

        /// <summary>A movie actually in progress (playing or paused) - tray/relaunch
        /// restore surfaces the player view instead of the library. A stopped or ended player
        /// restores Home, not a dead black frame.</summary>
        public bool IsPlayingMedia => _player.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening;

        public void SetFullscreen(bool on)
        {
            if (on == _fullscreen) return;
            _fullscreen = on;
            if (on) { ControlBar.MaxWidth = 1000; ControlBar.HorizontalAlignment = HorizontalAlignment.Center; ControlBar.CornerRadius = new CornerRadius(22); ControlBar.Margin = new Thickness(20, 20, 20, 44); }
            else { ControlBar.MaxWidth = double.PositiveInfinity; ControlBar.HorizontalAlignment = HorizontalAlignment.Stretch; ControlBar.CornerRadius = new CornerRadius(16); ControlBar.Margin = new Thickness(20); }
            FullscreenChanged?.Invoke(on);
            // The bar must be reachable right after the chrome switch (reported
            // missing-bar bug): show it once, then the idle timer owns hiding.
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
            FullscreenGlyph.Data = Geometry.Parse(on ? "M10,4 V10 H4 M18,4 V10 H24 M24,18 H18 V24 M4,18 H10 V24" : "M4,9.8 V5.7 A1.7,1.7 0 0 1 5.7,4 H9.8 M18.2,4 H22.3 A1.7,1.7 0 0 1 24,5.7 V9.8 M24,18.2 V22.3 A1.7,1.7 0 0 1 22.3,24 H18.2 M4,18.2 V22.3 A1.7,1.7 0 0 0 5.7,24 H9.8");
            FullscreenButton.ToolTip = on ? "Exit full screen (Esc)" : "Full screen (F)";
            ShowBarOnce();
        }

        private void OnFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!IsFullscreen);

        // ---------- bar styling (Settings, live) ----------

        private static void ApplyGlyphBrush(DependencyObject element, Brush brush)
        {
            if (element is System.Windows.Shapes.Path path)
            {
                if (path.Stroke != null) path.Stroke = brush;
                if (path.Fill != null) path.Fill = brush;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyGlyphBrush(VisualTreeHelper.GetChild(element, i), brush);
        }

        public void ApplyBarStyle()
        {
            _secondaryBackHighlight.Stop();_secondaryForwardHighlight.Stop();
            var style = _settings.BarColorStyle;
            var ring = style is "orange" or "twotone" ? Orange : RingDefault;
            var label = style is "orange" or "inverse" ? Orange : RingDefault;
            var times = Orange;

            SkipBackRing.Stroke = ring;
            SkipForwardRing.Stroke = ring;
            SkipBackArrow.Fill = ring;
            SkipForwardArrow.Fill = ring;
            SkipBackLabel.Foreground = label;
            SkipForwardLabel.Foreground = label;
            SkipBackLabel.Text = PlayerSettings.FormatSkip(_settings.PrimarySkipSeconds);
            SkipForwardLabel.Text = PlayerSettings.FormatSkip(_settings.PrimarySkipSeconds);
            SecondarySkipBackIcon.Stroke = ring;
            SecondarySkipForwardIcon.Stroke = ring;
            SecondarySkipBackLabel.Foreground = label;
            SecondarySkipForwardLabel.Foreground = label;
            // Settled units: minutes read as minutes (60 -> "1m"), never raw seconds.
            SecondarySkipBackLabel.Text = ShortSkip(_settings.SecondarySkipSeconds);
            SecondarySkipForwardLabel.Text = ShortSkip(_settings.SecondarySkipSeconds);
            SecondarySkipBackButton.ToolTip = $"Back {_settings.SecondarySkipSeconds} s";
            SecondarySkipForwardButton.ToolTip = $"Forward {_settings.SecondarySkipSeconds} s";

            var hollow = _settings.IconStyle == "hollow";
            PlayRing.Stroke = hollow ? ring : Brushes.Transparent;
            PlayRing.Fill = hollow ? Brushes.Transparent : ring;
            PlayGlyph.Fill = hollow ? ring : Dark;
            foreach (var child in PauseGlyph.Children)
                if (child is System.Windows.Shapes.Rectangle r) r.Fill = hollow ? ring : Dark;

            ElapsedText.Foreground = times;
            TotalText.Foreground = style == "original" ? Muted : times;
            RemainingText.Foreground = style == "orange" ? Orange : RingDefault;
            VolumeButton.Foreground = label;
            SpeedButton.Foreground = label;
            SleepButton.Foreground = label;
            CaptionsButton.Foreground = label;
            FullscreenButton.Foreground = label;
            foreach (var button in new[] { VolumeButton, SpeedButton, SleepButton, CaptionsButton, FullscreenButton })
                ApplyGlyphBrush(button, label);

            SpeedButton.Visibility = _settings.ShowSpeed ? Visibility.Visible : Visibility.Collapsed;
            SleepButton.Visibility = _settings.ShowSleepTimer ? Visibility.Visible : Visibility.Collapsed;
            RefreshCaptionsUi();
        }
    }
}
