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
        private static readonly SolidColorBrush Gold = new(Color.FromRgb(0xF0, 0xC2, 0x3E));
        private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x96, 0x96, 0xA0));
        private static readonly SolidColorBrush Dark = new(Color.FromRgb(0x1B, 0x1B, 0x1E));

        // Brushes are shared app-wide; Freeze makes them safe to touch from any
        // thread (each test case runs on its own STA thread - unfrozen statics
        // threw cross-thread Freezable exceptions, CI-observed).
        static MainWindow()
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
        private readonly VolumeService _volume;
        private readonly DispatcherTimer _idleTimer = new();
        private readonly DispatcherTimer _clock = new();

        private DispatcherTimer? _sleepTimer;
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

        /// <summary>Fired when the sleep timer ends playback; App lands on Home.</summary>
        public event Action? SleepFired;
        public event Action? HomeRequested;
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
            _volume = volume ?? new VolumeService();
            Loaded += (_, __) => { Video.MediaPlayer = _player; _cursorWatch.Start(); ShowBarOnce(); };
            IsVisibleChanged += (_, __) => { if (IsVisible) { _cursorWatch.Start(); ShowBarOnce(); } else _cursorWatch.Stop(); };
            Closed += (_, __) => { _cursorWatch.Stop(); StopPlaybackSource(); };
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
            _clock.Interval = TimeSpan.FromMilliseconds(500);
            _clock.Tick += (_, __) => UpdatePosition();

            _settings.Changed += (_, __) => Dispatcher.InvokeAsync(ApplyBarStyle);
            _settings.WriteFailed += (_, __) => Dispatcher.InvokeAsync(() => MessageBox.Show(this, "Saved settings are unreadable. The original file has been preserved, and changes cannot be saved until it is repaired.", "Settings could not be saved", MessageBoxButton.OK, MessageBoxImage.Warning));

            // Remembered volume restored on open. With the
            // tied-volume behavior, the slider IS Windows' master volume.
            _volumeSync = true;
            VolumeSlider.Value = _settings.LastVolume;
            _volumeSync = false;
            ApplyBarStyle();
            UpdatePlayPauseGlyph();
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
            _safeSource?.Abort(); _player.Stop(); _player.Media=null;
            var old = _safeSource; _safeSource = null;
            // Teardown off the caller thread: disposing the media waits on native
            // decoder threads and provider closes, and a blocked close/read must
            // never stall stop/cleanup on the UI.
            if (old != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => DisposeReplacedSource(old));
        }
        public async void PlayFromUrl(string url,long? positionSeconds,string[] subtitles)
        {
            try { await PlayFromUrlAsync(url,positionSeconds,subtitles); }
            catch (OperationCanceledException) { }
            catch { MovieTitle.Text="Could not open this movie safely. Check the file or phone connection."; }
        }
        public async System.Threading.Tasks.Task PlayFromUrlAsync(string url,long? positionSeconds,string[] subtitles)
        {
            var generation=++_playGeneration;
            _preparation?.Cancel();
            using var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            _preparation=timeout;
            SafeMediaSource source;
            try {source=await System.Threading.Tasks.Task.Run(() => {
                var ready=new SafeMediaSource(_libvlc,url,timeout.Token);
                try {foreach(var sub in subtitles)ready.AddSubtitle(sub,timeout.Token);ready.PreparationComplete();return ready;}
                catch {ready.Dispose();throw;}
            });}
            catch(OperationCanceledException) when(generation==_playGeneration) {throw new TimeoutException("Movie preparation timed out. Check the phone connection.");}
            finally {await Dispatcher.InvokeAsync(()=>{if(ReferenceEquals(_preparation,timeout))_preparation=null;});}
            await Dispatcher.InvokeAsync(()=>CompletePreparedPlayback(source,generation,timeout.IsCancellationRequested,url,positionSeconds));
        }
        private void CompletePreparedPlayback(SafeMediaSource source,int generation,bool canceled,string url,long? positionSeconds)
        {
            if(generation!=_playGeneration || canceled) {source.Dispose();throw new OperationCanceledException("Playback preparation was canceled or superseded.");}
            SavePlaybackProgress();
            _currentSource = url;
            var localTitle = PlaybackHistory.LocalPath(url);
            MovieTitle.Text = localTitle != null ? System.IO.Path.GetFileNameWithoutExtension(localTitle) : "Playing from phone";
            _lastHistorySave = DateTime.MinValue;
            // Restore the player's remembered loudness when a movie starts:
            // Windows master = the slider number, libVLC adds the boost on top.
            if (_volume.Available) _volume.SetMaster(_settings.LastVolume);
            ApplyPlayerGain();
            // Preserve the user's mute across the playback start: libVLC resets its
            // own mute state on a new Media, so carry Windows' truth over BEFORE play
            // instead of waiting for the next 500 ms poll (a half-second audio blip).
            var muteAtStart = _volume.TryGetMute();
            if (muteAtStart.HasValue) { _playerMute = muteAtStart.Value; _player.Mute = _playerMute; }
            // A failed read (null) keeps the player's previous mute: unknown is
            // never treated as "unmuted".
            var replaced = _safeSource;
            replaced?.Abort();
            _player.Stop();
            _player.Media=null;
            _safeSource = source;
            _player.Play(source.Media);
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

        /// <summary>Poll/first-play mute application: a failed read (null) keeps
        /// the player's CURRENT mute; only a successful read moves it. Pinned by
        /// VolumeMuteTests.</summary>
        internal static bool MuteAfterRead(bool current, bool? read) => read ?? current;

        // The player's mute as last APPLIED by this window. libvlc's Mute
        // read-back is unreliable with no media loaded or no audio device (it
        // returns false regardless of what was set), so preservation on a failed
        // endpoint read and the SharedMute fallback track this field instead of
        // reading _player.Mute back. Every assignment below keeps both in step.
        private bool _playerMute;
        /// <summary>Test seam (InternalsVisibleTo): the tracked player mute.</summary>
        internal bool TrackedPlayerMute { get => _playerMute; set => _playerMute = value; }

        private DateTime _lastMasterSave;
        private void UpdatePosition()
        {
            if (DateTime.UtcNow - _lastHistorySave >= TimeSpan.FromSeconds(5))
            { SavePlaybackProgress(); _lastHistorySave = DateTime.UtcNow; }
            // Volume tie, other direction: if Windows' volume changed elsewhere,
            // the bar follows so both places show the same number.
            // Mute is applied WITHOUT the Available gate: TryGetMute self-guards
            // (null when the endpoint is gone or the read failed, and null keeps
            // the tracked mute), so a flaky Available probe cannot disengage the
            // tie or clobber the state.
            _playerMute = MuteAfterRead(_playerMute, _volume.TryGetMute());
            _player.Mute = _playerMute;
            if (_volume.Available)
            {
                var master = _volume.GetMaster();
                if (master >= 0 && Math.Abs(master - VolumeSlider.Value) >= 1)
                {
                    _volumeSync = true;
                    VolumeSlider.Value = master;
                    _volumeSync = false;
                    ApplyPlayerGain();
                }
            }
            if (_volume.Available && DateTime.UtcNow - _lastMasterSave >= TimeSpan.FromSeconds(1))
            {
                var master = _volume.GetMaster();
                if (master >= 0 && _settings.LastVolume != master) _settings.LastVolume = master;
                _lastMasterSave = DateTime.UtcNow;
            }
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
        private void OnPlayerKeyDown(object sender, KeyEventArgs e)
        {
            ShowBarOnce();
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || _menuOpen) return;
            switch (e.Key)
            {
                case Key.Space: OnPlayPause(this, new RoutedEventArgs()); break;
                case Key.Left: OnSkipBack(this, new RoutedEventArgs()); break;
                case Key.Right: OnSkipForward(this, new RoutedEventArgs()); break;
                case Key.F: SetFullscreen(!IsFullscreen); break;
                case Key.Escape: if (IsFullscreen) SetFullscreen(false); break;
                case Key.M: OnMute(this, new RoutedEventArgs()); break;
                default: return;
            }
            e.Handled = true;
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

        // ---------- volume (tied to Windows master; boost from Settings) ----------

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML sets the slider Value during InitializeComponent, before the
            // ctor assigns _volume/_settings: a load-time change must be a no-op.
            if (_volume is null || _settings is null) return;
            if (_volumeSync) return;
            var v = (int)Math.Round(VolumeSlider.Value);
            if (_volume.Available) _volume.SetMaster(v);
            _settings.LastVolume = v;
            ApplyPlayerGain();
        }

        /// <summary>Phone-driven volume takes the same tied path as the slider.</summary>
        public void SetVolumeTied(int volume)
        {
            VolumeSlider.Value = Math.Clamp(volume, 0, 100);
            // OnVolumeChanged does the master + gain + save.
        }

        public int SharedVolume => (int)Math.Round(VolumeSlider.Value);
        public bool SharedMute => _volume.TryGetMute() ?? _playerMute;
        public string CurrentMovieTitle => MovieTitle.Text;

        private void ApplyPlayerGain()
        {
            // Slider 0-100 is the shared number; boost is extra libVLC gain on top
            // (100% boost = none, 200% = VLC-style doubling).
            _player.Volume = (int)(VolumeSlider.Value * _settings.VolumeBoostMaxPct / 100.0);
        }

        private void OnMute(object sender, RoutedEventArgs e)
        {
            var mute = !_playerMute;
            _playerMute = mute;
            _player.Mute = mute;
            if (_volume.Available) _volume.SetMute(mute);
            VolumeButton.Opacity = 1.0;
            VolumeGlyph.Data = Geometry.Parse(mute ? "M2,9 H6 L11,5 V21 L6,17 H2 Z M15,9 L23,17 M23,9 L15,17" : "M2,10 v6 h4 l5,4 V6 l-5,4 Z M14,9 a4,4 0 0 1 0,8");
        }

        /// <summary>Phone-driven mute takes the same tied path.</summary>
        public void SetMuteTied(bool mute)
        {
            _playerMute = mute;
            _player.Mute = mute;
            if (_volume.Available) _volume.SetMute(mute);
            VolumeButton.Opacity = 1.0;
            VolumeGlyph.Data = Geometry.Parse(mute ? "M2,9 H6 L11,5 V21 L6,17 H2 Z M15,9 L23,17 M23,9 L15,17" : "M2,10 v6 h4 l5,4 V6 l-5,4 Z M14,9 a4,4 0 0 1 0,8");
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

        private void OnCaptions(object sender, RoutedEventArgs e)
        {
            // Scaffold behavior: cycle subtitle tracks (off -> 1 -> 2 ... -> off).
            var tracks = _player.SpuDescription;
            if (tracks == null || tracks.Length == 0) return;
            var current = _player.Spu;
            var next = current == -1 ? tracks[0].Id : (current == tracks[^1].Id ? -1 : tracks[Array.FindIndex(tracks, t => t.Id == current) + 1].Id);
            _player.SetSpu(next);
        }

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
            if (on) { _previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; ControlBar.MaxWidth = 1000; ControlBar.HorizontalAlignment = HorizontalAlignment.Center; ControlBar.CornerRadius = new CornerRadius(22); ControlBar.Margin = new Thickness(20, 20, 20, 44); }
            else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; WindowState = _previousState; ControlBar.MaxWidth = double.PositiveInfinity; ControlBar.HorizontalAlignment = HorizontalAlignment.Stretch; ControlBar.CornerRadius = new CornerRadius(16); ControlBar.Margin = new Thickness(20); }
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
        }
    }
}
