using System;
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
        private MainWindow? _player;
        private HomeWindow? _home;
        private LibVLC? _libvlc;
        private MediaPlayer? _mediaPlayer;

        // Own identity, never shared with BingeMote (protocol.md Coexistence):
        // unique AppUserModelID (taskbar pinning) and single-instance mutex name.
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern void SetCurrentProcessExplicitAppUserModelID(string id);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            SetCurrentProcessExplicitAppUserModelID("Zephyr.Player");
            _singleInstance = new System.Threading.Mutex(true, "ZephyrPlayer", out bool createdNew);
            if (!createdNew)
            {
                // Already running: hand off quietly instead of doubling the listener.
                Shutdown();
                return;
            }
            Core.Initialize();
            var libvlc = new LibVLC();
            var player = new MediaPlayer(libvlc);
            var store = new PairedStore();
            var settings = new PlayerSettings();
            var gate = new PairingGate();

            _libvlc = libvlc;
            _mediaPlayer = player;
            var volume = new VolumeService();
            _player = new MainWindow(libvlc, player, settings, volume);
            _player.SleepFired += () =>
            {
                _player.Hide();
                if (_home != null) { _home.Show(); _home.Activate(); }
            };
            _player.Closing += (_, ev) =>
            {
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
                newPairCode: () => gate.NewCode());

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
                        store.Add(t);
                        return t;
                    }, out var token);
                    if (outcome == PairOutcome.Ok)
                    {
                        Current.Dispatcher.InvokeAsync(() =>
                        {
                            _player?.HidePairBanner();
                            _home?.HidePairBanner();
                        });
                    }
                    return (outcome, token);
                },
                input: new SendInputSink(),
                control: new MainWindowPlaybackControl(_player, player),
                settings: settings);
            _server.Start();
            // "Find my PC" on the phone (same UDP broadcast as the helper, our
            // own reply identity). Failure to bind only costs auto-discovery.
            try { _discovery = new DiscoveryResponder(_server.Port); }
            catch (Exception) { _discovery = null; }
            // Auto-update check on launch (silent on failure; private-phase feeds
            // simply answer "no update"). Result surfaces via /control/status.
            _ = Task.Run(() =>
            {
                UpdateState.Outcome = "checking";
                try
                {
                    var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
                    var hit = UpdateCheck.LatestNewerThanOrThrow(current);
                    UpdateState.Outcome = hit != null ? "available: " + hit.Value.Version : "up_to_date";
                }
                catch (Exception ex)
                {
                    // Diagnosable failure, not a fake "no update" (private-feed 404s land here too).
                    UpdateState.Outcome = "failed: " + ex.GetType().Name;
                }
            });
            if (!store.HasAny) _home.ShowPairBanner(gate.NewCode());
            _home.Show();
            // CI evidence args (local only): --open-settings shows the settings
            // modal over home; --play=<uri> starts local playback on the player.
            foreach (var arg in e.Args)
            {
                if (arg == "--open-settings")
                {
                    var sw = new SettingsWindow(settings, store, () => gate.NewCode()) { Owner = _home };
                    sw.Show();
                }
                else if (arg.StartsWith("--play=", StringComparison.Ordinal))
                {
                    var url = arg.Substring("--play=".Length);
                    _home.Hide();
                    _player.Show();
                    _player.PlayFromUrl(url, null, Array.Empty<string>());
                }
            }
        }

        private void EnsurePlayer() { } // single player window for app lifetime (Closing is cancelled)

        protected override void OnExit(ExitEventArgs e)
        {
            _discovery?.Dispose();
            _server?.Dispose();
            base.OnExit(e);
        }
    }
}
