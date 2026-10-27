using System;
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
        private readonly PlayerSettings _settings;
        private readonly VolumeService _volume;
        private readonly DispatcherTimer _idleTimer = new();
        private readonly DispatcherTimer _clock = new();
        private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private DispatcherTimer? _sleepTimer;
        private bool _seeking;
        private bool _volumeSync;

        /// <summary>Fired when the sleep timer ends playback; App lands on Home.</summary>
        public event Action? SleepFired;

        public MainWindow(LibVLC libvlc, MediaPlayer player, PlayerSettings? settings = null, VolumeService? volume = null)
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            _libvlc = libvlc;
            _player = player;
            _settings = settings ?? new PlayerSettings();
            _volume = volume ?? new VolumeService();
            Loaded += (_, __) => Video.MediaPlayer = _player;
            _idleTimer.Interval = TimeSpan.FromSeconds(3);
            _idleTimer.Tick += (_, __) =>
            {
                // VideoView hosts controls in a separate floating window. Never
                // hide the seek thumb while the pointer is using the bar. And a
                // PAUSED movie keeps the bar (review blocker 3): hiding it while
                // paused strands phone-only viewers with no visible controls.
                if (ControlBar.IsMouseOver || _seeking || _player.State == LibVLCSharp.Shared.VLCState.Paused) return;
                ControlBar.Opacity = 0;
                _idleTimer.Stop();
            };
            _clock.Interval = TimeSpan.FromMilliseconds(500);
            _clock.Tick += (_, __) => UpdatePosition();
            _flashTimer.Tick += (_, __) => { _flashTimer.Stop(); ApplyBarStyle(); };
            _settings.Changed += (_, __) => Dispatcher.InvokeAsync(ApplyBarStyle);

            // Remembered volume restored on open. With the
            // tied-volume behavior, the slider IS Windows' master volume.
            _volumeSync = true;
            VolumeSlider.Value = _settings.LastVolume;
            _volumeSync = false;
            ApplyBarStyle();
            UpdatePlayPauseGlyph();
        }

        public void PlayFromUrl(string url, long? positionSeconds, string[] subtitles)
        {
            // Restore the player's remembered loudness when a movie starts:
            // Windows master = the slider number, libVLC adds the boost on top.
            if (_volume.Available) _volume.SetMaster(_settings.LastVolume);
            ApplyPlayerGain();
            var media = new Media(_libvlc, url, FromType.FromLocation);
            foreach (var sub in subtitles)
                media.AddSlave(MediaSlaveType.Subtitle, 0, sub);
            _player.Play(media);
            if (positionSeconds.HasValue && positionSeconds.Value > 0)
                _player.Time = positionSeconds.Value * 1000;
            _clock.Start();
            // Phone-started playback shows the bar once (review blocker 3): the PC
            // mouse may never move, so the idle timer alone can't reveal it.
            ControlBar.Opacity = 1;
            _idleTimer.Stop();
            _idleTimer.Start();
            UpdatePlayPauseGlyph();
        }

        private void UpdatePosition()
        {
            // Volume tie, other direction: if Windows' volume changed elsewhere,
            // the bar follows so both places show the same number.
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

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ControlBar.Opacity = 1;
            _idleTimer.Stop();
            _idleTimer.Start();
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

        private void OnSecondarySkipBack(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(true, true), null, true);

        private void OnSecondarySkipForward(object sender, RoutedEventArgs e) => Skip(_settings.SkipDelta(true, false), null, false);

        private void Skip(int deltaSeconds, RotateTransform? spin, bool backward)
        {
            // Nothing loaded = nothing to skip: no time change, no spin, no flash.
            if (_player.Length <= 0) return;
            _player.Time = Math.Clamp(_player.Time + deltaSeconds * 1000L, 0, _player.Length);
            // Same language as the phone: the PRIMARY rings do one full turn per
            // tap (forward clockwise, back counter-clockwise), then settle
            // upright. The secondary double-triangles just flash their labels.
            if (spin != null)
            {
                spin.Angle = 0;
                var anim = new DoubleAnimation(backward ? -360 : 360, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                spin.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
            Flash();
        }

        private void Flash()
        {
            SkipBackLabel.Foreground = Orange;
            SkipForwardLabel.Foreground = Orange;
            SecondarySkipBackLabel.Foreground = Orange;
            SecondarySkipForwardLabel.Foreground = Orange;
            _flashTimer.Stop();
            _flashTimer.Start();
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

        private void ApplyPlayerGain()
        {
            // Slider 0-100 is the shared number; boost is extra libVLC gain on top
            // (100% boost = none, 200% = VLC-style doubling).
            _player.Volume = (int)(_settings.LastVolume * _settings.VolumeBoostMaxPct / 100.0);
        }

        private void OnMute(object sender, RoutedEventArgs e)
        {
            var mute = !_player.Mute;
            _player.Mute = mute;
            if (_volume.Available) _volume.SetMute(mute);
            VolumeButton.Opacity = mute ? 0.45 : 1.0;
        }

        /// <summary>Phone-driven mute takes the same tied path.</summary>
        public void SetMuteTied(bool mute)
        {
            _player.Mute = mute;
            if (_volume.Available) _volume.SetMute(mute);
            VolumeButton.Opacity = mute ? 0.45 : 1.0;
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
                        _player.Stop();
                        SleepFired?.Invoke();
                    };
                    _sleepTimer.Start();
                    SleepLabel.Text = min + "m";
                };
                menu.Items.Add(item);
            }
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
            ControlBar.Opacity = 1;
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
            WindowStyle = on ? WindowStyle.None : WindowStyle.SingleBorderWindow;
            WindowState = on ? WindowState.Maximized : WindowState.Normal;
            // The bar must be reachable right after the chrome switch (reported
            // missing-bar bug): show it once, then the idle timer owns hiding.
            ControlBar.Opacity = 1;
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        private void OnFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!IsFullscreen);

        // ---------- bar styling (Settings, live) ----------

        public void ApplyBarStyle()
        {
            var style = _settings.BarColorStyle;
            var ring = style is "orange" or "twotone" ? Orange : RingDefault;
            var label = style == "orange" ? Orange : RingDefault;
            var times = style == "orange" ? Orange : style == "original" ? Gold : RingDefault;

            SkipBackRing.Stroke = ring;
            SkipForwardRing.Stroke = ring;
            SkipBackArrow.Fill = ring;
            SkipForwardArrow.Fill = ring;
            SkipBackLabel.Foreground = label;
            SkipForwardLabel.Foreground = label;
            SkipBackLabel.Text = _settings.PrimarySkipSeconds.ToString();
            SkipForwardLabel.Text = _settings.PrimarySkipSeconds.ToString();
            SecondarySkipBackIcon.Fill = ring;
            SecondarySkipForwardIcon.Fill = ring;
            SecondarySkipBackLabel.Foreground = label;
            SecondarySkipForwardLabel.Foreground = label;
            // Settled units: minutes read as minutes (60 -> "1m"), never raw seconds.
            SecondarySkipBackLabel.Text = PlayerSettings.FormatSkip(_settings.SecondarySkipSeconds);
            SecondarySkipForwardLabel.Text = PlayerSettings.FormatSkip(_settings.SecondarySkipSeconds);

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

            SpeedButton.Visibility = _settings.ShowSpeed ? Visibility.Visible : Visibility.Collapsed;
            SleepButton.Visibility = _settings.ShowSleepTimer ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
