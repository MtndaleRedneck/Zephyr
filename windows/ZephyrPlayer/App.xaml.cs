using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;

namespace ZephyrPlayer
{
    public partial class App : Application
    {
        private PlaybackServer? _server;
        private DiscoveryResponder? _discovery;
        private System.Threading.Mutex? _singleInstance;
        private EventWaitHandle? _restoreSignal;
        private TrayService? _tray;
        private MainWindow? _player;
        private HomeWindow? _home;
        private PairingWindow? _pairing;
        private LibVLC? _libvlc;
        private MediaPlayer? _mediaPlayer;
        private PairingGate? _gate;
        private PairedStore? _store;
        private PlayerSettings? _settings;
        private bool _reallyQuit;
        private bool _trayHintShown;
        private volatile string? _pendingPhoneName;

        // Own identity, never shared with BingeMote (protocol.md Coexistence):
        // unique AppUserModelID (taskbar pinning) and single-instance mutex name.
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern void SetCurrentProcessExplicitAppUserModelID(string id);

        /// <summary>Startup/crash forensics: any unhandled failure lands in a
        /// temp log the CI capture + smoke steps print. Without this a WPF
        /// startup death on a headless runner is invisible.</summary>
        private static void LogCrash(string kind, string detail)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZephyrPlayer-crash.log");
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:O}] {kind}: {detail}\n\n");
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, ev) =>
                LogCrash("dispatcher", ev.Exception.ToString());
            AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
                LogCrash("unhandled", ev.ExceptionObject?.ToString() ?? "?");
            TaskScheduler.UnobservedTaskException += (_, ev) =>
                LogCrash("task", ev.Exception.ToString());
            SetCurrentProcessExplicitAppUserModelID("Zephyr.Player");
            _singleInstance = new System.Threading.Mutex(true, "ZephyrPlayer", out bool createdNew);
            if (!createdNew)
            {
                // Already running: wake the first instance's window (settled tray
                // behavior: relaunching ALWAYS restores) and hand off quietly.
                try
                {
                    if (EventWaitHandle.TryOpenExisting("ZephyrPlayer.Restore", out var signal))
                        signal.Set();
                }
                catch { }
                Shutdown();
                return;
            }
            // Windows never exit on their own anymore: closing hides to the tray,
            // and only the tray's Quit (or the updater) shuts the app down.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "ZephyrPlayer.Restore");
            var restoreListener = new Thread(() =>
            {
                while (true)
                {
                    try { _restoreSignal.WaitOne(); }
                    catch { return; }
                    if (_reallyQuit) return;
                    try { Dispatcher.InvokeAsync(RestoreApp); } catch { return; }
                }
            }) { IsBackground = true };
            restoreListener.Start();

            Core.Initialize();
            var libvlc = new LibVLC();
            var player = new MediaPlayer(libvlc);
            var store = new PairedStore();
            var settings = new PlayerSettings();
            var gate = new PairingGate();
            _libvlc = libvlc;
            _mediaPlayer = player;
            _store = store;
            _settings = settings;
            _gate = gate;

            // The tray must never take the app down: if the notification area
            // is unavailable the player still works, just without a tray icon.
            try
            {
                _tray = new TrayService();
                _tray.OpenRequested += RestoreApp;
                _tray.QuitRequested += QuitApp;
            }
            catch (Exception ex) { LogCrash("tray", ex.ToString()); _tray = null; }

            var volume = new VolumeService();
            _player = new MainWindow(libvlc, player, settings, volume);
            _player.SleepFired += () =>
            {
                _player.Hide();
                if (_home != null && !_reallyQuit) { _home.Show(); _home.Activate(); }
            };
            _player.Closing += (_, ev) =>
            {
                if (_reallyQuit) return; // real shutdown: let the window die
                // Player X never destroys the window: stop playback, hide it,
                // and land back on Home, the app's resting screen.
                ev.Cancel = true;
                player.Stop();
                _player.Hide();
                if (_home != null) { _home.Show(); _home.Activate(); }
            };

            _home = new HomeWindow(settings, store,
                playLocal: url =>
                {
                    EnsurePlayer();
                    _home?.Hide();
                    _player!.Show();
                    _player!.PlayFromUrl(url, null, Array.Empty<string>());
                },
                openPairing: ShowPairingWindow);
            _home.Closing += (_, ev) =>
            {
                if (_reallyQuit) return; // real shutdown: let the window die
                // Settled tray behavior: closing Home hides the app to the tray
                // instead of exiting; the icon brings it back.
                ev.Cancel = true;
                _home.Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray?.ShowStillRunningHint();
                }
            };

            // Pairing (protocol.md): the gate owns the code's lifetime, one-shot
            // spend, wrong-code lockout and the phone cap (helper pairing parity).
            _server = new PlaybackServer(
                onPlay: (url, pos, subs) =>
                    Current.Dispatcher.InvokeAsync(() =>
                    {
                        EnsurePlayer();
                        _home?.Hide();
                        _player!.Show();
                        _player!.PlayFromUrl(url, pos, subs);
                    }).Task,
                validateToken: store.IsPaired,
                onPair: code =>
                {
                    var outcome = gate.TryPair(code, store.Count, () =>
                    {
                        var t = Guid.NewGuid().ToString("N");
                        // The name typed on the Pairing screen is attached to the
                        // token the moment that code pairs.
                        store.Add(t, _pendingPhoneName ?? "My phone");
                        _pendingPhoneName = null;
                        return t;
                    }, out var token);
                    if (outcome == PairOutcome.Ok)
                    {
                        Current.Dispatcher.InvokeAsync(() =>
                        {
                            _pairing?.OnPairSucceeded();
                            _home?.Refresh();
                        });
                    }
                    return (outcome, token);
                },
                input: new SendInputSink(),
                control: new MainWindowPlaybackControl(_player, player),
                settings: settings);
            _server.Start();
            // "Find my PC" on the phone: the player's OWN discovery port (8490)
            // only. 8489 is never bound - the BingeMote helper keeps it
            // unconditionally, in both start orders. A failed own-port bind
            // surfaces in the UI instead of vanishing.
            try { _discovery = new DiscoveryResponder(_server.Port); ConnectionState.DiscoveryOff = false; }
            catch (Exception) { _discovery = null; ConnectionState.DiscoveryOff = true; }
            ConnectionState.NotifyChanged();
            // Public-Wi-Fi NOTICE (owner-settled: notice only, no Windows-setting
            // changes): at launch and whenever the network changes.
            NetworkCheck.CheckNow(_ => { ConnectionState.PublicWifiName = _.Name; ConnectionState.NotifyChanged(); });
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, __) =>
            {
                ConnectionState.PublicWifiName = null;
                ConnectionState.NotifyChanged();
                NetworkCheck.QueueCheck(p => { ConnectionState.PublicWifiName = p.Name; ConnectionState.NotifyChanged(); });
            };
            // Auto-update check on launch. Missing releases and failures remain
            // distinct from a verified current published installer.
            _ = Task.Run(() =>
            {
                UpdateState.Outcome = "checking";
                try
                {
                    var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
                    UpdateState.Outcome = UpdateCheck.CheckOutcomeOrThrow(current);
                }
                catch (Exception ex)
                {
                    // Diagnosable failure, not a fake "no update" (private-feed 404s land here too).
                    UpdateState.Outcome = "failed: " + ex.GetType().Name;
                }
            });
            _home.Show();
            // Owner requires a window that HAS BEEN SHOWN (InvalidOperationException
            // otherwise): Home goes up first, then the pairing screen over it.
            if (!store.HasAny) ShowPairingWindow();
            // CI evidence args (local only): --open-settings / --open-pairing show
            // those surfaces over home; --play=<uri> starts local playback.
            foreach (var arg in e.Args)
            {
                if (arg == "--open-settings")
                {
                    var sw = new SettingsWindow(settings, store, ShowPairingWindow) { Owner = _home };
                    sw.Show();
                }
                else if (arg == "--open-pairing")
                {
                    ShowPairingWindow();
                }
                else if (arg.StartsWith("--play=", StringComparison.Ordinal))
                {
                    var url = arg.Substring("--play=".Length);
                    _home.Hide();
                    _player.Show();
                    _player.PlayFromUrl(url, null, Array.Empty<string>());
                    if (System.Array.Exists(e.Args, a => a == "--fullscreen")) _player.SetFullscreen(true);
                }
            }
        }

        private void EnsurePlayer() { } // single player window for app lifetime (Closing is cancelled)

        /// <summary>The one Pairing screen, shared by launch, Home and Settings:
        /// every surface shows the SAME live gate.</summary>
        public void ShowPairingWindow()
        {
            if (_pairing != null)
            {
                _pairing.Show();
                _pairing.Activate();
                return;
            }
            _pairing = new PairingWindow(_gate!, _store!, name => _pendingPhoneName = name)
            {
                Owner = _home,
            };
            _pairing.Closed += (_, __) => _pairing = null;
            _pairing.Show();
            _pairing.Activate();
        }

        /// <summary>Tray icon / relaunch restore: bring the app back up. A movie on
        /// screen stays on top - never cover it with Home (review should-fix 5).</summary>
        private void RestoreApp()
        {
            if (_reallyQuit) return;
            if (_player != null && (_player.IsVisible || _player.IsPlayingMedia))
            {
                _player.Show();
                if (_player.WindowState == WindowState.Minimized) _player.WindowState = WindowState.Normal;
                _player.Activate();
                return;
            }
            if (_home != null)
            {
                _home.Show();
                _home.WindowState = WindowState.Normal;
                _home.Activate();
            }
        }

        private void QuitApp()
        {
            _reallyQuit = true;
            try { _pairing?.Close(); } catch { }
            try { _player?.Close(); } catch { }
            try { _home?.Close(); } catch { }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            _discovery?.Dispose();
            _server?.Dispose();
            _restoreSignal?.Dispose();
            base.OnExit(e);
        }
    }
}
