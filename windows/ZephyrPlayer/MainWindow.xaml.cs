using System;
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
    public partial class MainWindow : Window
    {
        private static readonly SolidColorBrush RingDefault = new(Color.FromRgb(0xF5, 0xF5, 0xF7));
        private static readonly SolidColorBrush Orange = new(Brand.Orange);
        private static readonly SolidColorBrush OrangeTint = new(Color.FromArgb(0x1A, 0xFF, 0x95, 0x00));
        private static readonly SolidColorBrush WhiteTint = new(Color.FromArgb(0x1A, 0xF5, 0xF5, 0xF7));
        private static readonly SolidColorBrush Gold = new(Color.FromRgb(0xF0, 0xC2, 0x3E));
        private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x96, 0x96, 0xA0));
        private static readonly SolidColorBrush Dark = new(Color.FromRgb(0x1B, 0x1B, 0x1E));

        // Brushes are shared app-wide; Freeze makes them safe to touch from any
        // thread (each test case runs on its own STA thread - unfrozen statics
        // threw cross-thread Freezable exceptions, CI-observed).
        static MainWindow()
        {
            RingDefault.Freeze();
            Orange.Freeze(); OrangeTint.Freeze(); WhiteTint.Freeze();
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

        private readonly DispatcherTimer _cursorWatch = new() { Interval = TimeSpan.FromMilliseconds(150) };
        [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
        private CursorPoint? _lastScreenCursor;
        private WindowState _previousState;
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

        public event Action? HomeRequested;

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (IsFullscreen) ControlBar.MaxWidth = FullscreenBarWidth(sizeInfo.NewSize.Width);
        }

        /// <summary>Half the window's current width, clamped to 760..1100, but never wider than the
        /// available width minus margins (a narrow or secondary monitor must not clip the bar).</summary>
        internal static double FullscreenBarWidth(double available) =>
            Math.Max(0, Math.Min(Math.Clamp(available * 0.5, 760, 1100), available - 40));

        private void OnHome(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

        public MainWindow(LibVLC libvlc, MediaPlayer player, PlayerSettings? settings = null, VolumeService? volume = null)
        {
            InitializeComponent();
            ((Canvas)SecondarySkipBackIcon.Parent).Children.Add(_secondaryBackHighlight);
            ((Canvas)SecondarySkipForwardIcon.Parent).Children.Add(_secondaryForwardHighlight);
            WindowChrome.ApplyDarkTitleBar(this);
            _libvlc = libvlc;
            _player = player;
            _settings = settings ?? new PlayerSettings();
            _ = volume; // 2.0.2: Windows' master volume is never used by the player; the parameter stays for callers.
            Loaded += (_, __) => { Video.MediaPlayer = _player; _cursorWatch.Start(); ShowBarOnce(); HookOverlayKeys(); };
            Overlay.Loaded += (_, __) => HookOverlayKeys();
            IsVisibleChanged += (_, __) => { if (IsVisible) { _cursorWatch.Start(); ShowBarOnce(); } else _cursorWatch.Stop(); };
            Closed += (_, __) => { _cursorWatch.Stop(); FlushVolumeSave(); StopPlaybackSource(); _closed = true; _awakeTimer.Stop(); _sleep.ReleaseNow(); };
            _cursorWatch.Tick += (_, __) =>
            {
                if (!IsVisible || !GetCursorPos(out var point)) return;
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
            _player.Playing += (_, __) => Dispatcher.BeginInvoke(() =>
            {
                _idleTimer.Stop(); _idleTimer.Start(); // resume the hide countdown
            });
            _awakeTimer.Tick += (_, __) => RefreshSleepGuard();
            _player.Playing += (_, __) => Dispatcher.BeginInvoke(RefreshSleepGuard);
            _player.Paused += (_, __) => Dispatcher.BeginInvoke(RefreshSleepGuard);
            _player.EncounteredError += (_, __) => Dispatcher.BeginInvoke(RefreshSleepGuard);
            StateChanged += (_, __) => RefreshSleepGuard();
            _player.Stopped += (_, __) => Dispatcher.BeginInvoke(RefreshSleepGuard);
            _player.EndReached += (_, __) => Dispatcher.BeginInvoke(RefreshSleepGuard);
            _clock.Interval = TimeSpan.FromMilliseconds(500);
            _clock.Tick += (_, __) => UpdatePosition();

            _settings.Changed += (_, __) => Dispatcher.InvokeAsync(() => { ApplyBarStyle(); RefreshSleepGuard(); });
            _settings.WriteFailed += (_, __) => Dispatcher.InvokeAsync(() => MessageBox.Show(this, "Saved settings are unreadable. The original file has been preserved, and changes cannot be saved until it is repaired.", "Settings could not be saved", MessageBoxButton.OK, MessageBoxImage.Warning));

            // 2.0.2 volume model: the player's OWN volume. Opens at the last
            // volume, or 100 when Remember volume is off. Windows' master volume and mute are
            // never read or written by the player.
            _volumeSync = true;
            VolumeSlider.Value = _settings.StartVolume;
            _volumeSync = false;
            RefreshVolumeUi();
            ApplyBarStyle();
            InitPopups();
            InitStartUi();
            MakeNonFocusable(Overlay);
            UpdatePlayPauseGlyph();
        }

        private readonly SleepGuard _sleep = new();
        private readonly System.Windows.Threading.DispatcherTimer _awakeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private bool _closed;
        /// <summary>Event-driven (play, pause, stop, end, error, minimize, setting change, close). The 2 second timer runs
        /// only while the wanted state and the actual hold differ (a failed take or a failed release), to retry it; it never polls otherwise,
        /// and nothing touches the player after the window is closed.</summary>
        private void RefreshSleepGuard()
        {
            if (_closed) return;
            var want = SleepGuard.ShouldHold(_settings.PreventSleep, IsPlayingMedia, WindowState == WindowState.Minimized);
            _sleep.Update(want);
            if (want != _sleep.Held) _awakeTimer.Start(); else _awakeTimer.Stop();
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
            DropEndOfMovieSleep();
            HideResumeNotice();
            DetachNativeStart(); SetOpening(false); EndLoading(); HideError();
            ++_playGeneration; _preparation?.Cancel();
            _safeSource?.Abort(); _player.Stop(); _player.Media=null;
            var old = _safeSource; _safeSource = null;
            // Teardown off the caller thread: disposing the media waits on native
            // decoder threads and provider closes, and a blocked close/read must
            // never stall stop/cleanup on the UI.
            if (old != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => DisposeReplacedSource(old));
        }
        private void CompletePreparedPlayback(SafeMediaSource source,int generation,bool canceled,string url,long? positionSeconds)
        {
            if(generation!=_playGeneration || canceled) {source.Dispose();throw new OperationCanceledException("Playback preparation was canceled or superseded.");}
            SavePlaybackProgress();
            _currentSource = url;
            MovieTitle.Text = TitleFor(url);
            _lastHistorySave = DateTime.MinValue;
            // The slider (the player's own volume) is the loudness; the boost adds decoder gain.
            ApplyPlayerGain();
            // Preserve the player's own mute across the playback start: libVLC resets its mute on
            // a new Media, so re-apply the tracked state BEFORE play.
            _player.Mute = _playerMute;
            var replaced = _safeSource;
            replaced?.Abort();
            _player.Stop();
            _player.Media=null;
            _safeSource = source;
            // Attach BEFORE Play: libVLC's Playing/Error events must reach this start's own handlers.
            AttachNativeStart(new NativeStart(generation, source));
            _player.Play(source.Media);
            _player.Mute = _playerMute;     // libVLC resets mute on a new Media
            // The speed the bar shows (_rate) carries across starts. libVLC keeps the rate on the MediaPlayer and a new
            // input inherits it; if this player ever reports another rate for the new media, the decoder is set back to
            // what the bar shows (no reopen). RateAtHandoff records what libVLC reported.
            RateAtHandoff = _player.Rate;
            if (Math.Abs(RateAtHandoff - (float)_rate) > 0.001f) { _player.SetRate((float)_rate); RateReapplyCount++; }
            // The replaced source tears down off the UI thread (see StopPlaybackSource).
            if (replaced != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => DisposeReplacedSource(replaced));
            if (positionSeconds.HasValue && positionSeconds.Value > 0)
            {
                _player.Time = positionSeconds.Value * 1000;
                if (_startOverGeneration == generation) ShowResumeNotice(positionSeconds.Value);
            }
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

        // ---------- "Resumed from 42:10 - Start over" ----------

        /// <summary>How long the resume notice stays before it fades (it also leaves on Start over, a new start or stop).</summary>
        internal static TimeSpan ResumeNoticeTime = TimeSpan.FromSeconds(8);
        private int _startOverGeneration;
        private System.Windows.Threading.DispatcherTimer? _resumeTimer;
        /// <summary>Test seam: the position the current start resumed from (null: none shown).</summary>
        internal long? ResumedFromSeconds { get; private set; }
        internal bool IsResumeNoticeShown => ResumeNotice.Visibility == Visibility.Visible;
        internal int StartOverCount { get; private set; }

        private void ShowResumeNotice(long seconds)
        {
            ResumedFromSeconds = seconds;
            ResumeText.Text = "Resumed from " + Format(seconds * 1000);
            ResumeNotice.BeginAnimation(OpacityProperty, null);
            ResumeNotice.Opacity = 1;
            ResumeNotice.Visibility = Visibility.Visible;
            _resumeTimer?.Stop();
            _resumeTimer = new System.Windows.Threading.DispatcherTimer { Interval = ResumeNoticeTime };
            _resumeTimer.Tick += (_, __) =>
            {
                _resumeTimer?.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
                fade.Completed += (_, ___) => { if (ResumeNotice.Opacity <= 0.01) HideResumeNotice(); };
                ResumeNotice.BeginAnimation(OpacityProperty, fade);
            };
            _resumeTimer.Start();
        }

        private void HideResumeNotice()
        {
            _resumeTimer?.Stop();
            _resumeTimer = null;
            ResumedFromSeconds = null;
            ResumeNotice.BeginAnimation(OpacityProperty, null);
            ResumeNotice.Opacity = 1;
            ResumeNotice.Visibility = Visibility.Collapsed;
        }

        /// <summary>Start over: back to 0:00 in the same start (no reopen).</summary>
        internal void StartOver()
        {
            HideResumeNotice();
            StartOverCount++;
            _player.Time = 0;
            if (_player.Length > 0) _history.Save(_currentSource, 0, _player.Length);
            UpdatePosition();
        }

        private void OnStartOver(object sender, RoutedEventArgs e) => StartOver();

        // The player's mute as last APPLIED by this window. libvlc's Mute
        // read-back is unreliable with no media loaded or no audio device (it
        // returns false regardless of what was set), so preservation on a failed
        // endpoint read and the SharedMute fallback track this field instead of
        // reading _player.Mute back. Every assignment below keeps both in step.
        private bool _playerMute;
        /// <summary>Test seam (InternalsVisibleTo): the tracked player mute.</summary>
        internal bool TrackedPlayerMute { get => _playerMute; set => _playerMute = value; }

        private void UpdatePosition()
        {
            if (DateTime.UtcNow - _lastHistorySave >= TimeSpan.FromSeconds(5))
            { SavePlaybackProgress(); _lastHistorySave = DateTime.UtcNow; }
            // The player's own mute and volume only: nothing here reads Windows' master volume or mute.
            _player.Mute = _playerMute;
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
        private void OnPlayerKeyDown(object sender, KeyEventArgs e) => HandleKey(e);

        /// <summary>The one key map. Keys reach it from this window and from LibVLCSharp's overlay window (which has focus
        /// after any click on the bar): Space, Left/Right skips, Up/Down volume, F, M, Esc (pop-up first, then full screen).
        /// Anything else, Alt/Ctrl/Win combinations and keys typed into a text box pass through unhandled.</summary>
        internal void HandleKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) != 0) return;
            if (key == Key.Escape && (_openPopup != null || _menuOpen)) { ClosePopups(); e.Handled = true; return; }
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

        private Window? _overlayWindow;

        /// <summary>LibVLCSharp hosts the Overlay in its own window; keys pressed there reach the same key map.</summary>
        private void HookOverlayKeys()
        {
            var overlay = Window.GetWindow(Overlay);
            if (overlay == null || ReferenceEquals(overlay, this) || ReferenceEquals(overlay, _overlayWindow)) return;
            _overlayWindow = overlay;
            overlay.PreviewKeyDown += (_, e) => { if (IsLoaded) HandleKey(e); };
        }

        /// <summary>Bar buttons and sliders never take keyboard focus: a click on one would strand the key map.</summary>
        private static void MakeNonFocusable(DependencyObject node)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Slider) ((FrameworkElement)node).Focusable = false;
            foreach (var child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject d) MakeNonFocusable(d);
        }

        /// <summary>Up/Down volume step, in slider percent.</summary>
        internal const int VolumeStep = 5;
        internal void StepVolume(int delta) => VolumeSlider.Value = Math.Clamp(Math.Round(VolumeSlider.Value) + delta, 0, 100);
        private void OnVideoDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!ControlBar.IsMouseOver) { SetFullscreen(!IsFullscreen); e.Handled = true; }
        }

        private void OnSeekDown(object sender, RoutedEventArgs e) { if (!_opening) _seeking = true; }

        private void OnSeekUp(object sender, RoutedEventArgs e)
        {
            _seeking = false;
            if (!_opening && _player.Length > 0)
            {
                LastSeekFraction = SeekBar.Value / 1000.0;
                _player.Position = (float)LastSeekFraction;
            }
        }

        // ---------- play / pause / skips ----------

        private void OnPlayPause(object sender, RoutedEventArgs e)
        {
            if (_opening) { PlayPauseRefusedCount++; return; }    // nothing reaches the old movie while a new start opens
            PlayPauseHandedCount++;
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
            if (_opening || _player.Length <= 0) return;
            _player.Time = Math.Clamp(_player.Time + deltaSeconds * 1000L, 0, _player.Length);
            var secondary = spin == SecondaryBackSpin || spin == SecondaryForwardSpin;
            var effects = new System.Collections.Generic.List<string>();
            // Press animations live behind "Animate on press" (rotate the ring only, the label stays
            // upright; the secondary arrow gets a light that runs to the tip). The colour flash
            // lives behind "Button flash".
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

        /// <summary>Test seam: what the last skip press animated ("spin", "light", "flash").</summary>
        internal string LastPressEffects { get; private set; } = "";

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

        // ---------- volume (the player's own volume; boost from Settings) ----------

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML sets the slider Value during InitializeComponent, before the
            // ctor assigns _volume/_settings: a load-time change must be a no-op.
            if (_settings is null || _player is null) return;
            ApplyPlayerGain();
            RefreshVolumeUi();
            if (_volumeSync) return;
            ScheduleVolumeSave();
        }

        // The remembered volume is saved once the slider rests (500 ms after the last step, and on
        // stop/quit), not on every tick of a drag, key or phone step.
        internal static TimeSpan VolumeSaveDelay = TimeSpan.FromMilliseconds(500);
        private System.Windows.Threading.DispatcherTimer? _volumeSave;
        private bool _volumeDirty;
        internal int VolumeSaveCount { get; private set; }

        private void ScheduleVolumeSave()
        {
            _volumeDirty = true;
            if (_volumeSave == null)
            {
                _volumeSave = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher) { Interval = VolumeSaveDelay };
                _volumeSave.Tick += (_, __) => FlushVolumeSave();
            }
            _volumeSave.Stop();
            _volumeSave.Interval = VolumeSaveDelay;
            _volumeSave.Start();
        }

        /// <summary>Write the remembered volume now if a change is pending.</summary>
        internal void FlushVolumeSave()
        {
            _volumeSave?.Stop();
            if (!_volumeDirty || _settings is null) return;
            _volumeDirty = false;
            VolumeSaveCount++;
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

        private void ApplyPlayerGain()
        {
            // Slider 0-100 is the shared number; boost is extra libVLC gain on top
            // (100% boost = none, 200% = VLC-style doubling).
            _player.Volume = (int)(VolumeSlider.Value * _settings.VolumeBoostMaxPct / 100.0);
        }

        private const string MutedGlyph = "M2,9 H6 L11,5 V21 L6,17 H2 Z M15,9 L23,17 M23,9 L15,17";
        private const string VolumeGlyphData = "M2,10 v6 h4 l5,4 V6 l-5,4 Z M14,9 a4,4 0 0 1 0,8";

        /// <summary>One place that draws volume state: glyph, "62%" or "Muted", and a dimmed slider while muted.</summary>
        private void RefreshVolumeUi()
        {
            if (VolumeLabel == null) return;
            VolumeButton.Opacity = 1.0;
            VolumeGlyph.Data = Geometry.Parse(_playerMute ? MutedGlyph : VolumeGlyphData);
            VolumeLabel.Text = _playerMute ? "Muted" : SharedVolume + "%";
            VolumeSlider.Opacity = _playerMute ? 0.45 : 1.0;
        }

        private void OnMute(object sender, RoutedEventArgs e)
        {
            var mute = !_playerMute;
            _playerMute = mute;
            _player.Mute = mute;
            RefreshVolumeUi();
        }

        /// <summary>Phone-driven mute: the player's own mute.</summary>
        public void SetMuteTied(bool mute)
        {
            _playerMute = mute;
            _player.Mute = mute;
            RefreshVolumeUi();
        }

        // ---------- speed + sleep timer (bar toggles, off by default) ----------

        /// <summary>Fullscreen overflow: speed, audio track and aspect ratio, so the floating bar stays compact.</summary>
        internal ContextMenu BuildMoreMenu()
        {
            var menu = new ContextMenu();
            var speed = new MenuItem { Header = "Speed" };
            foreach (var rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                var r = (float)rate;
                var item = new MenuItem { Header = rate + "x", IsChecked = Math.Abs(_rate - rate) < 0.01 };
                item.Click += (_, __) => SetRate(rate);
                speed.Items.Add(item);
            }
            menu.Items.Add(speed);
            var audio = new MenuItem { Header = "Audio" };
            var tracks = _player.AudioTrackDescription;
            if (tracks == null || tracks.Length == 0) audio.Items.Add(new MenuItem { Header = "No audio tracks", IsEnabled = false });
            else foreach (var t in tracks)
            {
                var id = t.Id;
                var item = new MenuItem { Header = string.IsNullOrWhiteSpace(t.Name) ? "Track " + id : t.Name, IsChecked = _player.AudioTrack == id };
                item.Click += (_, __) => _player.SetAudioTrack(id);
                audio.Items.Add(item);
            }
            menu.Items.Add(audio);
            var aspect = new MenuItem { Header = "Aspect ratio" };
            foreach (var (label, value) in new (string, string?)[] { ("Default", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9") })
            {
                var v = value;
                var item = new MenuItem { Header = label, IsChecked = _player.AspectRatio == v };
                item.Click += (_, __) => _player.AspectRatio = v;
                aspect.Items.Add(item);
            }
            menu.Items.Add(aspect);
            return menu;
        }

        private void OnMore(object sender, RoutedEventArgs e)
        {
            var menu = BuildMoreMenu();
            _menuOpen = true;
            menu.Closed += (_, __) => { _menuOpen = false; ShowBarOnce(); };
            menu.IsOpen = true;
        }

        // ---------- captions / fullscreen ----------

        /// <summary>Phone-initiated pause/resume shows the bar once (review blocker 3).</summary>
        public void ShowBarOnce()
        {
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        public bool IsFullscreen => WindowStyle == WindowStyle.None;

        /// <summary>A movie actually in progress (playing or paused) - tray/relaunch
        /// restore surfaces this window instead of Home. A stopped or ended player
        /// restores Home, not a dead black frame.</summary>
        public bool IsPlayingMedia => _player.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening;

        public void SetFullscreen(bool on)
        {
            if (on == IsFullscreen) return;
            if (on) { _previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; ControlBar.MaxWidth = FullscreenBarWidth(ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth); ControlBar.HorizontalAlignment = HorizontalAlignment.Center; ControlBar.CornerRadius = new CornerRadius(22); ControlBar.Margin = new Thickness(20, 20, 20, 44); }
            else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; WindowState = _previousState; ControlBar.MaxWidth = double.PositiveInfinity; ControlBar.HorizontalAlignment = HorizontalAlignment.Stretch; ControlBar.CornerRadius = new CornerRadius(16); ControlBar.Margin = new Thickness(20); }
            // The bar must be reachable right after the chrome switch (reported
            // missing-bar bug): show it once, then the idle timer owns hiding.
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = 1;
            Overlay.Cursor = Cursors.Arrow;
            TopOverlay.BeginAnimation(OpacityProperty, null); TopOverlay.Opacity = 1;
            FullscreenGlyph.Data = Geometry.Parse(on ? "M10,4 V10 H4 M18,4 V10 H24 M24,18 H18 V24 M4,18 H10 V24" : "M4,9.8 V5.7 A1.7,1.7 0 0 1 5.7,4 H9.8 M18.2,4 H22.3 A1.7,1.7 0 0 1 24,5.7 V9.8 M24,18.2 V22.3 A1.7,1.7 0 0 1 22.3,24 H18.2 M4,18.2 V22.3 A1.7,1.7 0 0 0 5.7,24 H9.8");
            MoreButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
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
            // The 2.0.2 colour matrix (original: white primary rings and labels, orange secondary arrows and
            // labels, orange Play), applied to the 3.0 controls.
            Brush priRing, priLabel, secRing, secLabel, play, icons;
            switch (style)
            {
                case "orange": priRing = priLabel = secRing = secLabel = play = Orange; icons = Orange; break;
                case "white": priRing = priLabel = secRing = secLabel = play = RingDefault; icons = RingDefault; break;
                case "twotone": priRing = secRing = Orange; priLabel = secLabel = RingDefault; play = Orange; icons = RingDefault; break;
                case "inverse": priRing = secRing = RingDefault; priLabel = secLabel = Orange; play = Orange; icons = Orange; break;
                default: priRing = priLabel = RingDefault; secRing = secLabel = Orange; play = Orange; icons = RingDefault; break;
            }
            var times = Orange;

            SkipBackRing.Stroke = priRing;
            SkipForwardRing.Stroke = priRing;
            SkipBackArrow.Fill = priRing;
            SkipForwardArrow.Fill = priRing;
            SkipBackLabel.Foreground = priLabel;
            SkipForwardLabel.Foreground = priLabel;
            SkipBackLabel.Text = PlayerSettings.FormatSkip(_settings.PrimarySkipSeconds);
            SkipForwardLabel.Text = PlayerSettings.FormatSkip(_settings.PrimarySkipSeconds);
            SecondarySkipBackIcon.Stroke = secRing;
            SecondarySkipForwardIcon.Stroke = secRing;
            SecondarySkipBackLabel.Foreground = secLabel;
            SecondarySkipForwardLabel.Foreground = secLabel;
            // Settled units: minutes read as minutes (60 -> "1m"), never raw seconds.
            SecondarySkipBackLabel.Text = ShortSkip(_settings.SecondarySkipSeconds);
            SecondarySkipForwardLabel.Text = ShortSkip(_settings.SecondarySkipSeconds);
            SecondarySkipBackButton.ToolTip = $"Back {_settings.SecondarySkipSeconds} s";
            SecondarySkipForwardButton.ToolTip = $"Forward {_settings.SecondarySkipSeconds} s";

            var hollow = _settings.IconStyle == "hollow";
            var playIsOrange = ReferenceEquals(play, Orange);
            PlayRing.Stroke = hollow ? play : Brushes.Transparent;
            PlayRing.Fill = hollow ? (playIsOrange ? OrangeTint : WhiteTint) : play;   // 2.0.2: 10 % tint inside the ring
            PlayGlyph.Fill = hollow ? play : Dark;
            foreach (var child in PauseGlyph.Children)
                if (child is System.Windows.Shapes.Rectangle r) r.Fill = hollow ? play : Dark;

            ElapsedText.Foreground = times;
            TotalText.Foreground = style == "original" ? Muted : times;
            RemainingText.Foreground = style == "orange" ? Orange : RingDefault;
            VolumeButton.Foreground = icons;
            SpeedButton.Foreground = icons;
            SleepButton.Foreground = icons;
            CaptionsButton.Foreground = icons;
            FullscreenButton.Foreground = icons;
            foreach (var button in new[] { VolumeButton, SpeedButton, SleepButton, CaptionsButton, FullscreenButton })
                ApplyGlyphBrush(button, icons);

            SpeedButton.Visibility = _settings.ShowSpeed ? Visibility.Visible : Visibility.Collapsed;
            SleepButton.Visibility = _settings.ShowSleepTimer ? Visibility.Visible : Visibility.Collapsed;
            RefreshSpeedUi();       // a non-normal speed or a running timer keeps its orange icon
            RefreshSleepUi();
        }
    }
}
