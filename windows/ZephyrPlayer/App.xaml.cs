using System.Linq;
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
        private readonly DisplayWatch _displayWatch = new();
        private MainWindow? _player;
        private HomeWindow? _home;
        private LibVLC? _libvlc;
        private MediaPlayer? _mediaPlayer;
        private PairingGate? _gate;
        private PairedStore? _store;
        private PlayerSettings? _settings;
        private bool _reallyQuit;
        private bool _startupDone;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;
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

        /// <summary>LibVLC with the subtitle look (S/M/L size, bold with an outline) and the hardware-decoding choice.
        /// libVLC 3 reads these once, at creation: this is where a subtitle size saved during an earlier session takes
        /// effect (the restart popup). If libVLC refuses the subtitle options, Zephyr still starts with the hardware
        /// choice alone, then with libVLC's defaults.</summary>
        internal static LibVLC CreateLibVlc(PlayerSettings settings) => CreateLibVlc(settings, out _, out _);

        /// <summary>[optionsUsed] are the options handed to the LibVLC constructor and [sizeUsed] the saved size they
        /// carry (null when libVLC refused them and its defaults are in use).</summary>
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
            var hardware = HardwareDecoding.Options(settings.HardwareDecoding);
            optionsUsed = hardware.Concat(PlayerBarLogic.SubtitleOptions(size, dpi)).ToArray();
            sizeUsed = size;
            try { return new LibVLC(optionsUsed); }
            catch (Exception ex)
            {
                LogCrash("libvlc-options", ex.GetType().Name);
                optionsUsed = hardware;
                sizeUsed = null;
                try { return new LibVLC(optionsUsed); }
                catch { optionsUsed = Array.Empty<string>(); return new LibVLC(); }
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, ev) =>
            {
                LogCrash("dispatcher", ev.Exception.ToString());
                // Once the app is up, an exception thrown by a UI handler is logged and the app keeps
                // running (the tray app is also the phone remote). Not during startup, not for
                // corrupting exceptions, and not when they repeat in a burst.
                if (_startupDone && CrashGuard.ShouldHandle(ev.Exception, DateTime.UtcNow))
                {
                    ev.Handled = true;
                    LogCrash("dispatcher-handled", "kept running after " + ev.Exception.GetType().Name);
                }
            };
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
                InputSender.BlockedStateChanged += OnInputBlocked;
            }
            catch (Exception ex) { LogCrash("tray", ex.ToString()); _tray = null; }

            var volume = new VolumeService();
            _player = new MainWindow(libvlc, player, settings, volume);
            _player.HomeRequested += () => { _player.SavePlaybackProgress(); _player.StopPlaybackSource(); _player.Hide(); _home?.Show(); _home?.Activate(); };
            _player.LibVlcSubtitleSize = libVlcSubtitleSize;
            _player.Closing += (_, ev) =>
            {
                if (_reallyQuit) return; // real shutdown: let the window die
                // Player X never destroys the window: stop playback, hide it,
                // and land back on Home, the app's resting screen.
                ev.Cancel = true;
                _player.SavePlaybackProgress();
                _player.StopPlaybackSource();
                _player.Hide();
                if (_home != null) { _home.Show(); _home.Activate(); }
            };

            _home = new HomeWindow(settings, store,
                playLocal: url =>
                {
                    EnsurePlayer();
                    _home?.Hide();
                    _player!.Show();
                    // A library start picks up at the saved spot (not in the first 10 s, not past 95 %) and says so with Start over.
                    var resume = PlaybackHistory.ResumeSeconds(new PlaybackHistory().Get(url));
                    _player!.PlayFromUrl(url, resume, Array.Empty<string>(), offerStartOver: resume.HasValue);
                },
                openPairing: ShowPairingWindow);
            Dispatcher.BeginInvoke(new Action(() => _startupDone = true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            // One remembered window geometry for Home and the player window.
            WindowGeometry.Attach(_home, settings);
            WindowGeometry.Attach(_player, settings);
            _home.Closing += (_, ev) =>
            {
                if (_reallyQuit) return; // real shutdown: let the window die
                // Settled tray behavior: closing Home hides the app to the tray
                // instead of exiting; the icon brings it back.
                ev.Cancel = true;
                _home.Hide();
            };

            // Pairing (protocol.md): the gate owns the code's lifetime, one-shot
            // spend, wrong-code lockout and the phone cap (helper pairing parity).
            // The one shell handoff for ANY playback start: Home hides, the player
            // window shows. /play and /control/play both go through it (protocol.md
            // gives them the same payload - starting a URL must always be visible).
            SelfUpdate.CleanOldDownloads();
            SafeMediaSource.CleanOldSubtitles();
            System.Threading.Tasks.Task ShowPlayerAndPlay(string url, long? pos, string[] subs)
            {
                EnsurePlayer();
                _home?.Hide();
                _player!.Show();
                return _player!.PlayFromUrlAsync(url, pos, subs);
            }
            // Display state for the wake-only first press. Registration failure disables the gate
            // (a remote stuck on "unknown" would swallow every press), and says so in the crash log.
            _displayWatch.Start();
            if (!_displayWatch.Available) LogCrash("display-watch", "Display power notification unavailable; wake-only first press is off.");
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
                isPaired: t => store.IsPaired(t),
                onPair: (code, source) =>
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
                        }, out token, source);
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
                display: _displayWatch.CreateGate(),
                control: new MainWindowPlaybackControl(_player, player,
                    showForPlayback: () => { EnsurePlayer(); _home?.Hide(); _player!.Show(); }),
                settings: settings,
                secure: true);
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
            // Public-Wi-Fi NOTICE (notice only, no Windows-setting
            // changes): at launch and whenever the network changes.
            NetworkCheck.CheckNow(_ => { ConnectionState.PublicWifiName = _.Name; ConnectionState.NotifyChanged(); });
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, __) =>
            {
                ConnectionState.PublicWifiName = null;
                ConnectionState.NotifyChanged();
                NetworkCheck.QueueCheck(p => { ConnectionState.PublicWifiName = p.Name; ConnectionState.NotifyChanged(); });
            };
            // Automatic update check: at launch, every 6 hours while the app runs (it lives in the tray
            // for days), and when it comes back from the tray (RestoreApp, at most every 30 minutes). A later
            // failed check keeps an offer already found.
            _ = BackgroundUpdateCheck.Run(first: true);
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = BackgroundUpdateCheck.Interval };
            _updateTimer.Tick += (_, __) => _ = BackgroundUpdateCheck.Run();
            _updateTimer.Start();
            SelfUpdate.Quit = QuitApp;     // the updater quits through the app's own Quit
            _home.Show();
            // Owner requires a window that HAS BEEN SHOWN (InvalidOperationException
            // otherwise): Home goes up first, then the pairing screen over it.
            SettingsWindow.PairingFactory = () => new PairingPanel(_gate!, store, name => _pendingPhoneName = name);
            if (!store.HasAny) _home.OpenSettings("remote");
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
                    _home.OpenSettings("volume");
                }
                else if (arg == "--tray-menu")
                {
                    _tray?.ShowMenuForCapture();
                }
                else if (arg == "--open-pairing")
                {
                    var capturePair = new PairingWindow(gate, store, name => _pendingPhoneName = name) { Owner = _home };
                    capturePair.Show();
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
#endif
        }

        private void EnsurePlayer() { } // single player window for app lifetime (Closing is cancelled)

        /// <summary>The one Pairing screen, shared by launch, Home and Settings:
        /// every surface shows the SAME live gate.</summary>
        public void ShowPairingWindow()
        {
            _home?.OpenSettings("remote");
        }

        /// <summary>Tray icon / relaunch restore: bring the app back up. A movie on
        /// screen stays on top - never cover it with Home (review should-fix 5).</summary>
        private void RestoreApp()
        {
            if (_reallyQuit) return;
            if (BackgroundUpdateCheck.DueOnRestore(DateTime.UtcNow)) _ = BackgroundUpdateCheck.Run();
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
            _player?.SavePlaybackProgress();
            _reallyQuit = true;
            try { _player?.Close(); } catch { }
            try { _home?.Close(); } catch { }
            Shutdown();
        }

        // Windows did not play phone input (for example an administrator window is in front).
        // Observed fact only; no cause is claimed beyond the hint in the notice.
        private void OnInputBlocked(bool blocked)
        {
            try { Dispatcher.BeginInvoke(new Action(() => _tray?.SetInputBlocked(blocked))); } catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            InputSender.BlockedStateChanged -= OnInputBlocked;
            _updateTimer?.Stop();
            _tray?.Dispose();
            _displayWatch.Dispose();
            _discovery?.Dispose();
            _server?.Dispose();
            _restoreSignal?.Dispose();
            base.OnExit(e);
        }
    }

    /// <summary>Which UI-thread exceptions the app survives (logged, Handled). Not the ones that mean the
    /// process itself is damaged, and not more than a few in a short burst (a handler that throws on
    /// every frame would otherwise spin forever).</summary>
    internal static class CrashGuard
    {
        internal const int BurstLimit = 5;
        internal static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(30);
        private static readonly System.Collections.Generic.Queue<DateTime> Recent = new();
        private static readonly object Sync = new();

        internal static bool IsCorrupting(Exception ex) =>
            ex is OutOfMemoryException || ex is StackOverflowException || ex is AccessViolationException
            || ex is InvalidProgramException || ex is BadImageFormatException || ex is TypeInitializationException
            || ex is System.Runtime.InteropServices.SEHException || ex is System.Threading.ThreadAbortException
            || (ex.InnerException != null && IsCorrupting(ex.InnerException));

        internal static bool ShouldHandle(Exception ex, DateTime nowUtc)
        {
            if (IsCorrupting(ex)) return false;
            lock (Sync)
            {
                while (Recent.Count > 0 && nowUtc - Recent.Peek() > BurstWindow) Recent.Dequeue();
                if (Recent.Count >= BurstLimit) return false;
                Recent.Enqueue(nowUtc);
                return true;
            }
        }

        internal static void ResetForTest() { lock (Sync) Recent.Clear(); }
    }
}
