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
        private ShellWindow? _shell;
        private PlayerView? _player;
        private HomeView? _home;
        private SettingsWindow? _settingsWin;
        private LibVLC? _libvlc;
        private MediaPlayer? _mediaPlayer;
        private PairingGate? _gate;
        private PairedStore? _store;
        private PlayerSettings? _settings;
        private bool _reallyQuit;
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
                // Retention: keep the log bounded - once past 256 KB keep only the
                // newest half instead of appending forever.
                var info = new System.IO.FileInfo(path);
                if (info.Exists && info.Length > 256 * 1024)
                {
                    var all = System.IO.File.ReadAllText(path);
                    System.IO.File.WriteAllText(path, "[... older entries trimmed ...]\n\n" + all.Substring(all.Length / 2));
                }
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:O}] {kind}: {detail}\n\n");
            }
            catch { }
        }

        /// <summary>LibVLC with the CP2 subtitle look (lifted above the bar, S/M/L size,
        /// bold with an outline). libVLC 3 reads these once, at creation: this is where a
        /// subtitle size saved during an earlier session takes effect (the restart popup,
        /// CP2 round 4). If libVLC refuses them, Zephyr still starts with its defaults.</summary>
        internal static LibVLC CreateLibVlc(PlayerSettings settings) => CreateLibVlc(settings, out _, out _);

        /// <summary>[optionsUsed] are the options handed to the LibVLC constructor and
        /// [sizeUsed] the saved size they carry (null when libVLC refused them and its
        /// defaults are in use).</summary>
        internal static LibVLC CreateLibVlc(PlayerSettings settings, out string[] optionsUsed, out string? sizeUsed)
        {
            double dpi = 1.0;
            try
            {
                using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
                dpi = g.DpiY / 96.0;
            }
            catch { dpi = 1.0; }
            var size = settings.SubtitleSize;
            optionsUsed = PlayerBarLogic.SubtitleOptions(size, dpi);
            sizeUsed = size;
            try { return new LibVLC(optionsUsed); }
            catch (Exception ex)
            {
                LogCrash("libvlc-options", ex.GetType().Name);
                optionsUsed = Array.Empty<string>();
                sizeUsed = null;
                return new LibVLC();
            }
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
            // Elevated setup/teardown copies run no UI and exit (round-6 N-8). They
            // must come BEFORE the single-instance mutex: the uninstaller re-elevates
            // the cleanup while the running app still holds it, and the setup copy
            // runs while the explaining parent is alive.
            if (e.Args.Length >= 1 && e.Args[0] == "--install-firewall")
            {
                Shutdown(FirewallSetup.RunElevated(e.Args.Length > 1 ? e.Args[1] : null));
                return;
            }
            if (e.Args.Length >= 1 && e.Args[0] == "--remove-firewall")
            {
                Shutdown(FirewallSetup.RemoveElevated(e.Args.Length > 1 ? e.Args[1] : null));
                return;
            }
#if PAIR_CODE_HOOK
            // CI only (compiled out of shipped builds): the upgrade smoke plants the
            // 1.1.0-shaped rules and asserts this says "not current" before migration
            // and "current" after - the Player's unelevated rejection proof, headless
            // (round-9 N9-6). An EARLY arg check, before the mutex and any UI.
            if (e.Args.Length >= 1 && e.Args[0] == "--firewall-shape-ok")
            {
                // The hooked exe runs from a capture folder; the UDP rule names the
                // INSTALLED exe. The smoke passes that path as the second arg.
                var expectedExe = e.Args.Length >= 2 ? e.Args[1] : (Environment.ProcessPath ?? "");
                Shutdown(FirewallSetup.RulesHaveCurrentShapeFor(expectedExe) ? 0 : 1);
                return;
            }
            // CI only: render a permission dialog with real interaction state to a PNG,
            // then shut down from the hook (round-9 missing-captures proof for the
            // hover/pressed/focus template fix). Early arg check, before the mutex.
            if (e.Args.Length >= 3 && e.Args[0] == "--dialog-shot")
            {
                FirewallSetup.RunDialogShot(e.Args[1], e.Args[2]);
                return; // RunDialogShot shuts the app down after the capture
            }
#endif
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
            var settings = new PlayerSettings();
            var libvlc = CreateLibVlc(settings, out _, out var libVlcSubtitleSize);
            var player = new MediaPlayer(libvlc);
            player.EnableMouseInput = false;
            player.EnableKeyInput = false;
            var store = new PairedStore();
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

            // 2.0 (P0-1, P0-2): ONE ShellWindow for the app's life. Library and player
            // are views swapped in place inside it - no window hides to show another.
            // Volume is the player's own (owner decision 2): no Windows master service.
            _shell = new ShellWindow(settings);
            _player = new PlayerView(libvlc, player, settings) { LibVlcSubtitleSize = libVlcSubtitleSize };
            _player.HomeRequested += LeavePlayer;
            // Sleep timer: the movie pauses and the player stays (render 04 footnote);
            // nothing navigates on it.
            _player.FullscreenChanged += on => _shell.ApplyFullscreen(on);
            _home = new HomeView(settings, store,
                playLocal: url =>
                {
                    ShowPlayer();
                    // P0-3: local movies bring their own sidecar subtitles along.
                    _player!.PlayFromUrl(url, null, SafeMediaSource.LocalSidecars(url));
                },
                openPairing: ShowPairingWindow,
                openSettings: OpenSettings);
            _shell.ShowView(_home);
            _shell.ViewKeyDown += (_, ev) => { if (ReferenceEquals(_shell.CurrentView, _player)) _player!.HandleKey(ev); };
            _shell.Closing += (_, ev) =>
            {
                if (_reallyQuit) { _shell.SaveBounds(); return; } // real shutdown: let the window die
                // Close is not quit (owner decision 1): the app stays in the tray for
                // phone starts, and the bar leaves WITH the window (P0-1).
                ev.Cancel = true;
                CloseToTray();
            };

            // Pairing (protocol.md): the gate owns the code's lifetime, one-shot
            // spend, wrong-code lockout and the phone cap (helper pairing parity).
            // The one shell handoff for ANY playback start: the shell (shown if it was
            // in the tray) swaps to the player view in place. /play and /control/play
            // both go through it (protocol.md gives them the same payload).
            SelfUpdate.CleanOldDownloads();
            SafeMediaSource.CleanOldSubtitles();
            System.Threading.Tasks.Task ShowPlayerAndPlay(string url, long? pos, string[] subs)
            {
                ShowPlayer();
                return _player!.PlayFromUrlAsync(url, pos, subs);
            }
            _server = new PlaybackServer(
                onPlay: (url, pos, subs) =>
                    Current.Dispatcher.InvokeAsync(() => ShowPlayerAndPlay(url, pos, subs)).Task.Unwrap(),
                // CI-only capture seam (round-5 P-1): the capture script acts as the
                // phone over REAL HTTP for the phone-start bar proof. The fixed token
                // is COMPILED OUT unless the CI_CAPTURE_HOOK constant is defined -
                // only the dedicated capture build sets it; the shipped binary has
                // no known credential at all.
                validateToken: t =>
                {
                    if (store.ValidateAndTouch(t)) return true;
#if CI_CAPTURE_HOOK
                    if (Environment.GetEnvironmentVariable("ZEPHYR_CI_CAPTURE") != null && t == "ci-capture-token") return true;
#endif
                    return false;
                },
                onPair: code =>
                {
                    // Fail-closed store: no code is consumed and the phone gets a
                    // plain answer instead of a 500 (round-3 P4).
                    if (store.IsBroken) return (PairOutcome.StoreUnavailable, null);
                    PairOutcome outcome;
                    string? token;
                    try
                    {
                        outcome = gate.TryPair(code, store.Count, () =>
                        {
                            var t = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                            // The name typed on the Pairing screen is attached to the
                            // token the moment that code pairs.
                            store.Add(t, _pendingPhoneName ?? "My phone");
                            _pendingPhoneName = null;
                            return t;
                        }, out token);
                    }
                    catch (InvalidOperationException)
                    {
                        // The save failed: the transactional store changed nothing and
                        // the gate never spent the code, so a retry is safe.
                        return (PairOutcome.StoreUnavailable, null);
                    }
                    if (outcome == PairOutcome.Ok)
                    {
                        Current.Dispatcher.InvokeAsync(() =>
                        {
                            _home?.Refresh();
                        });
                    }
                    return (outcome, token);
                },
                input: new SendInputSink(),
                control: new MainWindowPlaybackControl(_player, player,
                    showForPlayback: ShowPlayer),
                settings: settings);
            // Firewall + listen permission (round-6 N-8): the per-user installer never
            // adds them, so the FIRST run explains and asks Windows once. Declining
            // closes the app cleanly (retry on next launch) instead of failing the
            // listen with a misleading error.
            // ACTUAL live shape verification on every start - no marker shortcut: a
            // 1.1.0 upgrade keeps same-named rules with the old shape, and name-only
            // checks would never rerun setup (round-7 B7-1).
            if (!FirewallSetup.SetupIsCurrent())
            {
                if (FirewallSetup.LastReadFailed)
                {
                    // The firewall/ACL API could not be read at all: elevation cannot
                    // fix that, so no setup prompt - one plain note and close
                    // (round-9 N9-8).
                    FirewallSetup.Note("Windows Firewall settings could not be read on this PC. Zephyr Player cannot check its network permission and will close now; a restart of Windows usually fixes the read.");
                    _reallyQuit = true;
                    Shutdown();
                    return;
                }
                if (!FirewallSetup.ExplainAndRelaunch())
                {
                    _reallyQuit = true;
                    Shutdown();
                    return;
                }
            }
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
            _shell.Show();
            // Owner requires a window that HAS BEEN SHOWN (InvalidOperationException
            // otherwise): the shell goes up first, then Settings on Paired phones over it.
            SettingsWindow.PairingFactory = () => new PairingPanel(_gate!, store, name => _pendingPhoneName = name);
            if (!store.HasAny) OpenSettings("remote");
            // CI evidence args: COMPILED OUT of the shipped binary (round-6 S6-7/G6;
            // review nit 3 - a release binary must not grow a second Settings window
            // or autoplay from stray args). Only the dedicated capture build defines
            // CI_CAPTURE_HOOK, and it additionally opts in per run with
            // ZEPHYR_CI_CAPTURE.
#if CI_CAPTURE_HOOK
            var ciCapture = Environment.GetEnvironmentVariable("ZEPHYR_CI_CAPTURE") != null;
            foreach (var arg in ciCapture ? e.Args : Array.Empty<string>())
            {
                if (arg == "--open-settings")
                {
                    OpenSettings("volume");
                }
                else if (arg == "--tray-menu")
                {
                    _tray?.ShowMenuForCapture();
                }
                else if (arg == "--open-pairing")
                {
                    var capturePair = new PairingWindow(gate, store, name => _pendingPhoneName = name) { Owner = _shell };
                    capturePair.Show();
                }
                else if (arg.StartsWith("--play=", StringComparison.Ordinal))
                {
                    var url = arg.Substring("--play=".Length);
                    ShowPlayer();
                    _player.PlayFromUrl(url, null, SafeMediaSource.LocalSidecars(url));
                    if (System.Array.Exists(e.Args, a => a == "--fullscreen")) _player.SetFullscreen(true);
                }
            }
#endif
        }

        /// <summary>Put the shell on the player view. A shell in the tray is shown
        /// FIRST and then swapped, so LibVLCSharp's overlay never appears over a
        /// hidden owner.</summary>
        private void ShowPlayer()
        {
            if (_shell == null || _player == null) return;
            // Settings is owned by the shell, so it would sit over the movie: a
            // playback start (phone pick right after pairing, or a tile) closes it.
            try { _settingsWin?.Close(); } catch { }
            if (!_shell.IsVisible) _shell.Show();
            if (_shell.WindowState == WindowState.Minimized) _shell.WindowState = WindowState.Normal;
            _shell.ShowView(_player);
            _shell.Activate();
        }

        /// <summary>Back / sleep / tray-close from the player: save, stop, hide the
        /// overlay now, and swap to the library in the same window.</summary>
        private void LeavePlayer()
        {
            if (_shell == null || _player == null || _home == null) return;
            _player.SavePlaybackProgress();
            if (_player.IsFullscreen) _player.SetFullscreen(false);
            // CP1 repair 3: the library is laid out under the player and the video host
            // shows its picture BEFORE the movie stops and the overlay goes, so nothing
            // black is uncovered on the way back (ShellWindow.ShowView).
            var player = _player;
            _shell.ShowView(_home, () =>
            {
                player.StopPlaybackSource();
                player.HideOverlayWindow();
            });
        }

        /// <summary>Window X: never quits (tray). If a movie is up: save progress, stop
        /// the source, swap to the library (the overlay unloads and hides), and only
        /// then hide the shell - a cancelled Closing skips LibVLCSharp's own cleanup.</summary>
        private void CloseToTray()
        {
            if (_shell == null) return;
            if (ReferenceEquals(_shell.CurrentView, _player)) LeavePlayer();
            _player?.HideOverlayWindow();
            _shell.SaveBounds();
            _shell.Hide();
        }

        /// <summary>The one Settings window: compact, separate, owned by the shell,
        /// non-modal (an open Settings window must not freeze playback controls).</summary>
        public void OpenSettings(string section)
        {
            if (_settings == null || _store == null || _shell == null) return;
            if (_settingsWin != null) { _settingsWin.SelectSection(section); _settingsWin.Activate(); return; }
            _settingsWin = new SettingsWindow(_settings, _store, ShowPairingWindow) { Owner = _shell };
            _settingsWin.Closed += (_, __) => _settingsWin = null;
            _settingsWin.SelectSection(section);
            _settingsWin.Show();
        }

        /// <summary>The one Pairing screen, shared by launch, the library and Settings:
        /// every surface shows the SAME live gate (Settings > Paired phones).</summary>
        public void ShowPairingWindow()
        {
            OpenSettings("remote");
        }

        /// <summary>Tray icon / relaunch restore: the same shell comes back on the view
        /// it was on (library after a close from the player; the player when a
        /// phone-started movie is up).</summary>
        private void RestoreApp()
        {
            if (_reallyQuit || _shell == null) return;
            _shell.Show();
            if (_shell.WindowState == WindowState.Minimized) _shell.WindowState = WindowState.Normal;
            _shell.Activate();
        }

        private void QuitApp()
        {
            _player?.SavePlaybackProgress();
            _reallyQuit = true;
            try { _player?.HideOverlayWindow(); _player?.StopPlaybackSource(); } catch { }
            try { _settingsWin?.Close(); } catch { }
            try { _shell?.Close(); } catch { }
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
