// Zephyr VLC Remote Helper: the small Windows companion for the Zephyr VLC Remote Android app.
// It listens on the home network for mouse, scroll, zoom and keyboard commands from the
// paired phone and plays them back with SendInput. See README.md for setup.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinForms = System.Windows.Forms;

[assembly: InternalsVisibleTo("ZephyrVlcRemote.Tests")]

namespace ZephyrVlcRemote;

internal static class Program
{
    internal const int HttpPort = 8492;      // BingeMote keeps 8488; Zephyr Player owns 47823
    internal const int DiscoveryPort = 8491; // BingeMote keeps 8489, Zephyr Player owns 8490
    internal const string DiscoverMagic = "BINGEMOTE_DISCOVER_V1"; // the shared wire question every Zephyr discovery responder answers
    internal const string ReplyMagic = "ZEPHYR_VLC_REMOTE_HERE_V1";
    internal const string FirewallRule = "Zephyr VLC Remote";

    /// <summary>Private-networks-only inbound rule for vlc.exe (the web remote the app sets up).</summary>
    internal const string VlcFirewallRule = "Zephyr VLC Remote VLC";
    /// <summary>The 1.x rule name, removed by the firewall setup so upgrades do not leave it behind.</summary>
    internal const string LegacyFirewallRule = "Zephyr VLC Remote Helper";

    private static readonly string LocalRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The data folder: the new name once it exists, the 1.x name until the one-time
    /// move runs. The move itself waits for the single-instance mutex in Main, so two copies
    /// racing at sign-in never fight over it.</summary>
    internal static string DataDir { get; private set; } = PickDataDir();

    /// <summary>Test-only seam (InternalsVisibleTo): points the store at a temp folder so
    /// save/retry/concurrency tests never touch the real profile. Not an env or argument
    /// hook - production behavior is unchanged.</summary>
    internal static void RedirectDataDirForTests(string dir) { DataDir = dir; }
    /// <summary>Restore the real data dir after a redirected test (round-7 N8: a leaked
    /// redirect makes the NEXT suite write its config into a deleted temp folder).</summary>
    internal static void ResetDataDirForTests() { DataDir = PickDataDir(); }

    private static string PickDataDir()
    {
        var dir = Path.Combine(LocalRoot, "Zephyr VLC Remote");
        var old = Path.Combine(LocalRoot, "ZephyrVlcRemote");
        return Directory.Exists(dir) || !Directory.Exists(old) ? dir : old;
    }

    /// <summary>The data folder is %LocalAppData%\Zephyr VLC Remote - a fresh store: this app
    /// One move carries pairing, the movies folder and the log over. Called from Main only by
    /// the copy holding the single-instance mutex.</summary>
    private static void MigrateDataDir()
    {
        var dir = Path.Combine(LocalRoot, "Zephyr VLC Remote");
        var old = Path.Combine(LocalRoot, "ZephyrVlcRemote");
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(dir))
            {
                Directory.Move(old, dir);
                DataDir = dir;
            }
        }
        catch { /* the next start tries again */ }
    }

    internal static string ConfigPath => Path.Combine(DataDir, "config.json");
    private static string FirewallMarker => Path.Combine(DataDir, "firewall.ok");

    private static void StampFirewallMarker()
    {
        try { File.WriteAllText(FirewallMarker, FirewallSetup.MarkerVersion + "\n" + DateTime.UtcNow.ToString("O")); } catch { }
    }
    private static string QuitMarkerPath => Path.Combine(DataDir, "quit-on-purpose");
    /// <summary>Fresh while an update installer runs: the watchdog skips its round so it cannot
    /// start a copy mid-install. Any normal start deletes it.</summary>
    internal static string UpdatingMarkerPath => Path.Combine(DataDir, "updating");
    /// <summary>Set after the tray notice about a failed listen, so the 5-minute watchdog does
    /// not repeat it; cleared on the next normal start (one notice per sign-in).</summary>
    private static string ListenFailNoticePath => Path.Combine(DataDir, "listen-fail-noticed");

    private const string ShowEventName = @"Local\ZephyrVlcRemote.ShowWindow";
    private const string QuitEventName = @"Local\ZephyrVlcRemote.Quit";

    /// <summary>A phone paired to this PC. The name is typed in the window when pairing starts.</summary>
    internal sealed class PairedPhone
    {
        public string Token { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>The one-shot VLC-password fetch was consumed (adopt flow). Persisted,
        /// so a helper restart never re-arms it.</summary>
        public bool AdoptSpent { get; set; }
        /// <summary>The in-flight adopt fetch's request ID (persisted at serve time, so a
        /// helper restart replays/spends from the record instead of re-arming the grant).
        /// The password value itself is never stored here.</summary>
        public string? AdoptRequestId { get; set; }
        public DateTime? AdoptServedAtUtc { get; set; }
    }

    /// <summary>One entry per paired phone. Pairing adds one; Remove takes one out.</summary>
    internal static readonly List<PairedPhone> Phones = new();
    /// <summary>Guards Phones: the window mutates it on the UI thread while listener threads enumerate it.</summary>
    internal static readonly object PhonesLock = new();
    internal static PairedPhone[] PhonesSnapshot() { lock (PhonesLock) { return Phones.ToArray(); } }
    /// <summary>An existing config file that could not be read. Fail closed (round-5 B3):
    /// the file is PRESERVED, reads look unpaired, no pairing code is minted, and
    /// SaveConfig refuses - a torn file is never replaced by a fresh empty one.</summary>
    internal static bool StoreBroken;

    /// <summary>Why the store failed closed ("could not be read (...)" / "could not be
    /// decrypted"): shown in the window so a transient read error is never described
    /// as corruption. Never contains reset advice - no move/rename/reset step is
    /// suggested anywhere without the owner's explicit OK.</summary>
    internal static string StoreBrokenReason = "could not be read";
    internal static int PhonesCount { get { lock (PhonesLock) { return Phones.Count; } } }
    /// <summary>The name typed for the phone that is about to pair; consumed by a successful pairing.</summary>
    internal static string? PendingPhoneName;
    /// <summary>The window re-reads the phone list on the UI thread when this fires (any thread).</summary>
    internal static Action? PhonesChanged;
    /// <summary>Wi-Fi names where he chose "Not now" on the public-network prompt; not asked again.</summary>
    internal static readonly List<string> DeclinedNetworks = new();
    /// <summary>Wi-Fi names he marked as home. A home network is never buried by "Not now":
    /// if it shows as Public again, it is asked about again.</summary>
    internal static readonly List<string> HomeNetworks = new();
    /// <summary>More phones than this on one PC is a mistake, not a household.</summary>
    internal const int MaxPhones = 8;
    internal static bool Paired => PhonesCount > 0;
    /// <summary>The folder the phone's Movies tab opens; set from this window or from the phone.</summary>
    internal static string? MoviesFolder;
    internal static string? PairCode;
    /// <summary>The version that last ran; a change means an upgrade just landed.</summary>
    internal static string? LastRunVersion;
    /// <summary>A newer published helper version, found by the background update check.</summary>
    internal static (string Version, string ExeUrl, string? Sha256)? AvailableUpdate;
    internal static DateTime PairCodeExpires = DateTime.MinValue;
    internal static int PairFailures;
    internal const int MaxPairFailures = 5;
    internal static readonly TimeSpan PairCodeLifetime = TimeSpan.FromMinutes(10);

    [STAThread]
    private static int Main(string[] args)
    {
        // Visual styles + DPI awareness before ANY UI (the explained elevation and the
        // listen-failure notices can run before the window exists).
        ApplicationConfiguration.Initialize();
        if (args.Contains("--install-firewall"))
        {
            // Optional args from the unelevated parent: the USER's SID and data folder,
            // so the listen permission and the markers land with the user even when
            // another admin's credentials approved the prompt.
            var i = Array.IndexOf(args, "--install-firewall");
            var sid = i + 1 < args.Length ? args[i + 1] : null;
            var dir = i + 2 < args.Length ? args[i + 2] : null;
            // Logs from this elevated copy belong in the USER's data folder, not the
            // approving admin's profile.
            if (!string.IsNullOrWhiteSpace(dir)) DataDir = dir;
            return FirewallSetup.RunElevated(sid, dir);
        }
        if (args.Contains("--remove-firewall"))
        {
            // Elevated cleanup at uninstall: our rules and OUR listen permission only.
            var ri = Array.IndexOf(args, "--remove-firewall");
            var rsid = ri + 1 < args.Length ? args[ri + 1] : null;
            return FirewallSetup.RemoveElevated(rsid);
        }

#if PAIR_CODE_HOOK
        // CI only: the e2e plants old-shaped rules and asserts this says "not current"
        // before migration and "current" after - the unelevated detection, headless.
        // Must be an EARLY arg check: nested inside --window-shot it fell through to a
        // normal start and hung CI on the setup dialog (round-9 R9-2). Compiled out of
        // shipped builds with the other hooks.
        if (args.Contains("--firewall-shape-ok"))
            return FirewallSetup.RulesHaveCurrentShape() ? 0 : 1;
#endif

#if WINDOW_SHOT_HOOK
        // CI-only: render the window (Wi-Fi strip + first-run line visible) to a PNG and exit.
        if (args.Contains("--window-shot"))
        {
            Directory.CreateDirectory(DataDir);
            LoadConfig();
            lock (PhonesLock) { Phones.Clear(); }
            using var shot = new HelperWindow(windowShot: true);
            var shotPath = Environment.GetEnvironmentVariable("ZEPHYR_VLC_REMOTE_WINDOW_SHOT")
                ?? Path.Combine(DataDir, "window.png");
#if PAIR_CODE_HOOK
            // CI only: render specific window states for pixel review.
            var shotMode = Environment.GetEnvironmentVariable("ZEPHYR_VLC_REMOTE_WINDOW_SHOT_MODE");
            if (shotMode == "pairing-expired")
            {
                // The expired-code state (born-expired code, like the e2e hook).
                Program.PendingPhoneName = "Shot Phone";
                Program.PairCode = Program.NewPairCode();
                Program.PairCodeExpires = DateTime.UtcNow - TimeSpan.FromSeconds(1);
                shot.RunStatusTickForShot();
            }
            else if (shotMode == "pairing")
            {
                // A live code showing, exactly as after tapping Pair.
                Program.PendingPhoneName = "Shot Phone";
                Program.PairCode = Program.NewPairCode();
                Program.PairCodeExpires = DateTime.UtcNow + Program.PairCodeLifetime;
                shot.RunStatusTickForShot();
            }
            else if (shotMode == "broken-store")
            {
                // The fail-closed screen: saved pairings unreadable, no reset advice.
                Program.StoreBroken = true;
                Program.StoreBrokenReason = "could not be read";
                shot.RunStatusTickForShot();
            }
            else if (shotMode == "remove" || shotMode == "remove-all")
            {
                // The destructive confirms, real construction (round-9 missing captures).
                var text = shotMode == "remove"
                    ? "Remove Shot Phone? It stops controlling this PC through Zephyr VLC Remote right away. "
                      + "One thing stays: VLC's own remote (port 8080) uses one shared password, so this phone could still reach VLC directly until the VLC password changes. "
                      + "You can pair the phone again any time with a new code."
                    : "Remove all phones? Every phone stops controlling this PC through Zephyr VLC Remote right away. "
                      + "One thing stays: VLC's own remote (port 8080) uses one shared password, so a phone could still reach VLC directly until the VLC password changes. "
                      + "Pair them again with new codes.";
                using var d = ConfirmDialog.Build(text, shotMode == "remove" ? "Remove" : "Remove all");
                CaptureDialogForShot(d, shotPath);
                return 0;
            }
            else if (shotMode == "tray")
            {
                shot.ShowTrayMenuForShot(shotPath);
                return 0;
            }
            else if (shotMode == "remove-paths")
            {
                // PRODUCTION path proof (round-10 regression: Build returned a disposed
                // form and the real Ask crashed on Remove/Remove all). Runs the REAL
                // ConfirmDialog.Ask: cancel must return false, confirm must return true.
                const string text = "Remove Shot Phone? It stops controlling this PC through Zephyr VLC Remote right away. "
                    + "You can pair the phone again any time with a new code.";
                var cancelSays = AskWithAutoClick(text, "Remove", DialogResult.Cancel);
                Console.Error.WriteLine("[remove-paths] cancel path returned " + cancelSays + " (want False)");
                var confirmSays = AskWithAutoClick(text, "Remove", DialogResult.OK);
                Console.Error.WriteLine("[remove-paths] confirm path returned " + confirmSays + " (want True)");
                if (cancelSays || !confirmSays)
                {
                    Console.Error.WriteLine("[remove-paths] FAIL - production Ask returned the wrong answer");
                    return 2;
                }
                Console.Error.WriteLine("[remove-paths] PASS - production Ask opens, cancel cancels, confirm confirms");
                return 0;
            }
#endif
            shot.ShowOffscreenAndCapture(shotPath);
            return 0;
        }
#endif



        // The watchdog relaunches us with --ensure-running every few minutes: stay silent when
        // the app already runs or when he quit it on purpose. Any real launch (double-click,
        // sign-in autostart, installer, update, crash restart) cancels a purposeful quit.
        var ensureRunning = args.Contains("--ensure-running");
        if (ensureRunning)
        {
            if (File.Exists(QuitMarkerPath)) return 0;
            // A fresh update marker means the installer is running right now: starting the app
            // mid-install could fight it, so the watchdog skips this round.
            try
            {
                if (File.Exists(UpdatingMarkerPath)
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(UpdatingMarkerPath) < TimeSpan.FromMinutes(3))
                    return 0;
            }
            catch { }
            // Off in Settings > Startup apps means off: the watchdog honors the Windows switch.
            if (StartupDisabledByUser()) return 0;
        }
        else
        {
            try { File.Delete(QuitMarkerPath); } catch { }
            try { File.Delete(UpdatingMarkerPath); } catch { }
            try { File.Delete(ListenFailNoticePath); } catch { }
        }
        // Sign-in autostart and the Windows crash-restart are automatic launches: when a copy
        // is already up they stay quiet instead of counting as "opened by hand" and popping
        // the window. They still count as real launches for the quit marker (a sign-in revives
        // a tray quit).
        var quietLaunch = ensureRunning || args.Contains("--autostart") || args.Contains("/restarted");

        // One copy at a time. What a second launch does depends on what is already running:
        // the same exe just pops the running copy's window up; a different exe (an update)
        // asks the old copy to quit and takes over; a copy too old to have the events
        // (1.0.0/1.0.1) has to be quit by hand first.
        using var single = new Mutex(true, @"Local\ZephyrVlcRemote.SingleInstance", out var onlyCopy);
        if (!onlyCopy)
        {
            if (quietLaunch) return 0; // automatic launch: another copy is up, nothing to do, no window
            var runningPath = RunningCopyPath();
            var selfPath = Environment.ProcessPath;
            var sameExe = runningPath != null && selfPath != null &&
                string.Equals(runningPath, selfPath, StringComparison.OrdinalIgnoreCase);
            // A different exe is an update only when this launch is the same or a newer
            // version; an older exe (e.g. left in Downloads) must not downgrade the running copy.
            var isDowngrade = false;
            if (!sameExe && runningPath != null && selfPath != null)
            {
                try
                {
                    var mine = FileVersionInfo.GetVersionInfo(selfPath).FileVersion;
                    var theirs = FileVersionInfo.GetVersionInfo(runningPath).FileVersion;
                    if (mine != null && theirs != null &&
                        new Version(mine).CompareTo(new Version(theirs)) < 0) isDowngrade = true;
                }
                catch { /* cannot tell versions - treat as an update, as before */ }
            }
            if (sameExe || isDowngrade)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Log(isDowngrade
                    ? "An older exe launched while a newer copy ran - showed the running copy's window."
                    : "Launched while already running - asked the running copy to show its window.");
                return 0;
            }
            EventWaitHandle? oldQuit = null;
            try { oldQuit = EventWaitHandle.OpenExisting(QuitEventName); } catch { }
            if (oldQuit == null)
            {
                Log("An older copy (before 1.1.0) is running - asked him to quit it by hand.");
                NoteDialog.Show(null,
                    "An older Zephyr VLC Remote is already running. Right-click its icon by the clock (click the ^ arrow if you don't see it) and choose Quit, then open this one again.");
                return 0;
            }
            Log("Update launched while an older copy ran - asked it to quit, taking over.");
            oldQuit.Set();
            // The old copy exits and releases the mutex; take it over (give it a few seconds).
            var tookOver = false;
            for (var i = 0; i < 40 && !tookOver; i++)
            {
                try { tookOver = single.WaitOne(250); } catch (AbandonedMutexException) { tookOver = true; }
            }
            if (!tookOver)
            {
                Log("The older copy did not exit in time - giving up.");
                NoteDialog.Show(null,
                    "The older Zephyr VLC Remote is still closing. Open this one again in a few seconds.");
                return 0;
            }
        }

        MigrateDataDir(); // the copy holding the mutex owns the one-time 1.x folder move
        Directory.CreateDirectory(DataDir);
        LoadConfig();
        if (LastRunVersion != VersionText)
        {
            // An upgrade just landed. Windows caches icons aggressively; tell the shell
            // associations changed so the desktop shortcut and taskbar pin pick up the
            // new icon now instead of after a sign-out.
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x3000 /* SHCNF_IDLIST|SHCNF_FLUSH */, IntPtr.Zero, IntPtr.Zero); } catch { }
            LastRunVersion = VersionText;
            try { SaveConfig(); } catch (Exception ex) { Log("Could not save the version stamp: " + ex.Message); }
        }
        Log("Started. " + (!Paired ? "Not paired - showing a pairing code." : "Paired (" + PhonesCount + (PhonesCount == 1 ? " phone" : " phones") + ") - starting tucked in the tray."));
        RegisterAutostart();
        try { RegisterApplicationRestart("/restarted", 0); } catch { } // crash or hang: Windows relaunches us

    {
        // The live check runs on EVERY start (COM read, no admin): a versioned marker
        // must never vouch for rules deleted or changed after it was written, and a
        // marker file can even be copied between machines (round-8). The marker is
        // still stamped (the upgrade smoke asserts it) but nothing trusts it.
        FirewallSetup.LastReadFailed = false;
        if (FirewallSetup.RulesHaveCurrentShape() && FirewallSetup.UrlAclNamesCurrentUser())
        {
            StampFirewallMarker();
        }
        else
        {
            // Watchdog launch: never pop the admin prompt every 5 minutes - stay quiet
            // until he opens the app himself and finishes the one-time setup.
            if (ensureRunning) { Log("Watchdog start skipped: firewall setup not done yet."); return 0; }
            if (FirewallSetup.LastReadFailed)
            {
                // The firewall/ACL API could not be read at all: elevation cannot fix
                // that, so no setup prompt - one plain note and close (round-9 N9-8).
                Log("firewall/ACL read failed at startup; not offering elevation.");
                NoteDialog.Show(null, "Windows Firewall settings could not be read on this PC. Zephyr VLC Remote cannot check its network permission and will close now; a restart of Windows usually fixes the read.");
                return 0;
            }
            // Any decline or failure: one dark note was already shown. Leave cleanly
            // (even if a leftover listen permission would let us bind) and offer the
            // setup again on the next launch.
            if (!FirewallSetup.ExplainAndRelaunch()) return 0;
            if (FirewallSetup.RulesHaveCurrentShape() && FirewallSetup.UrlAclNamesCurrentUser())
                StampFirewallMarker();
            else
            {
                // The elevation reported success but the re-check disagrees: say so in
                // plain words instead of vanishing silently (round-8 N-2).
                NoteDialog.Show(null,
                    "Setup did not finish, so the phone cannot reach this PC yet. Zephyr VLC Remote will now close and offer to set up again next time you open it.");
                return 0;
            }
        }
        // Surface the vlc.exe rule inventory the elevated step saved (report only).
        FirewallSetup.LogVlcRuleInventory();
    }

        // Resume places: load the saved ones and watch VLC while it runs. This must run
        // on EVERY start, not only the first (round-5 B4): inside the firewall block it
        // never ran again once the marker existed, and the next save wiped older places.
        ResumeTracker.Start();

        // A live pairing code is a way in, so one exists only when this PC still needs
        // pairing. After pairing, a new code comes only from the window's Pair button.
#if PAIR_CODE_HOOK
        // CI only: a fixed pairing code for the e2e. The READ itself stays inside the
        // define so shipped builds carry neither the call nor the literal (round-10
        // B10-3); shipped builds never honor a fixed code.
        var overrideCode = Environment.GetEnvironmentVariable("ZEPHYR_VLC_REMOTE_PAIR_CODE");
#else
        string? overrideCode = null;
#endif
        if (overrideCode is { Length: 6 } && overrideCode.All(char.IsDigit))
        {
            PairCode = overrideCode;
            PairCodeExpires = DateTime.UtcNow + PairCodeLifetime;
#if PAIR_CODE_HOOK
            // CI only: exercise the expiry path without waiting out the lifetime.
            if (Environment.GetEnvironmentVariable("ZEPHYR_VLC_REMOTE_PAIR_CODE_EXPIRES_PAST") == "1")
                PairCodeExpires = DateTime.UtcNow - TimeSpan.FromSeconds(1);
#endif
        }
        else if (!Paired && !StoreBroken)
        {
            PendingPhoneName = null;
            PairCode = NewPairCode();
            PairCodeExpires = DateTime.UtcNow + PairCodeLifetime;
        }

        using var http = new HttpServer();
        using var discovery = new DiscoveryResponder();
        CheckForUpdate();
        try { http.Start(); discovery.Start(); }
        catch (Exception ex) {
            Log("Could not start listening: " + ex.Message);
            // Watchdog: no pop-up loop, but not silence either - one quiet tray notice per sign-in.
            if (ensureRunning) { NotifyListenFailureOnce(); return 1; }
            NoteDialog.Show(null, "Could not start listening for the phone. If Windows asked for administrator permission earlier, choose Yes - it is needed once to open the port.\n\n" + ex.Message);
            return 1;
        }
        RegisterWatchdog(); // only a working app gets the watchdog: no 5-minute relaunch loop after a failed setup

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var window = new HelperWindow();
        _ = window.Handle; // force handle creation so a second launch can always signal us
        var watcher = new Thread(() =>
        {
            var events = new WaitHandle[] { showEvent, quitEvent };
            while (true)
            {
                try
                {
                    var which = WaitHandle.WaitAny(events);
                    if (window.IsDisposed) return;
                    if (which == 1)
                    {
                        // A newer copy launched: step aside so it can take over the mutex.
                        Log("A newer copy launched - quitting so it can take over.");
                        window.BeginInvoke(new Action(Application.Exit));
                        return;
                    }
                    window.BeginInvoke(new Action(window.ShowFromTray));
                }
                catch { return; }
            }
        }) { IsBackground = true };
        watcher.Start();
        Application.Run(window);
        Log("Exited.");
        return 0;
    }

    /// <summary>True when the copy holding the mutex runs from the same exe file as this launch.</summary>
    /// <summary>The exe path of the other running copy, or null when there is none or it cannot be read.</summary>
    private static string? RunningCopyPath()
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("ZephyrVlcRemote"))
            {
                if (proc.Id == me) continue;
                try { return proc.MainModule?.FileName; } catch { return null; }
            }
        }
        catch { }
        return null;
    }

    internal static string NewPairCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    internal static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>One line in %LocalAppData%\Zephyr VLC Remote\helper.log, so a bad launch leaves a trail.</summary>
    internal static void Log(string message)
    {
        try
        {
            var path = Path.Combine(DataDir, "helper.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1_000_000)
            {
                var lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines.Skip(lines.Length / 2)); // keep the newer half
            }
            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch { /* logging must never break the helper */ }
    }

    /// <summary>Parse every pairing record format (1.3 "phones", 1.2 "tokens", 1.0/1.1
    /// single "token"). Returns false when any stored token cannot be decrypted - the
    /// caller fails the whole store closed rather than dropping that phone's record on
    /// the next save. Pure: reads the JSON only, never touches the file.</summary>
    internal static bool TryParsePhones(JsonElement root, out List<PairedPhone> phones, out string? unreadableName)
    {
        phones = new List<PairedPhone>();
        unreadableName = null;
        if (root.TryGetProperty("phones", out var ps))
            foreach (var p in ps.EnumerateArray())
            {
                var traw = p.TryGetProperty("token", out var pt) ? pt.GetString() : null;
                var n = p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                if (traw == null) { unreadableName = n; return false; }
                var tok = Dpapi.Unwrap(traw);
                if (tok == null) { unreadableName = n; return false; }
                var spent = p.TryGetProperty("adoptSpent", out var asp) && asp.GetBoolean();
                string? adoptReq = p.TryGetProperty("adoptRequestId", out var ari) ? ari.GetString() : null;
                DateTime? adoptAt = null;
                if (p.TryGetProperty("adoptServedAtUtc", out var asa) && asa.GetString() is string asas
                    && DateTime.TryParse(asas, null, System.Globalization.DateTimeStyles.RoundtripKind, out var asad))
                    adoptAt = asad;
                phones.Add(new PairedPhone { Token = tok, Name = string.IsNullOrWhiteSpace(n) ? "Phone " + (phones.Count + 1) : n, AdoptSpent = spent, AdoptRequestId = adoptReq, AdoptServedAtUtc = adoptAt });
            }
        // 1.2 stored bare tokens without names; those phones show as "Phone 1" etc.
        if (phones.Count == 0 && root.TryGetProperty("tokens", out var ts))
            foreach (var el in ts.EnumerateArray())
            {
                var vraw = el.GetString();
                if (vraw == null) return false;
                var v = Dpapi.Unwrap(vraw);
                if (v == null) return false;
                phones.Add(new PairedPhone { Token = v, Name = "Phone " + (phones.Count + 1) });
            }
        // 1.0/1.1 configs stored one token; keep that phone paired.
        if (phones.Count == 0 && root.TryGetProperty("token", out var old))
        {
            var vraw = old.GetString();
            if (vraw != null)
            {
                var v = Dpapi.Unwrap(vraw);
                if (v == null) return false;
                phones.Add(new PairedPhone { Token = v, Name = "Phone 1" });
            }
        }
        return true;
    }

    internal static void LoadConfig()
    {
        // Settled migration: a first run on a PC that had the BingeMote helper keeps the
        // phone's VLC pairings plus the Wi-Fi and movies-folder choices. Read-only on the
        // legacy side; it never runs once we have a config of our own.
        if (!File.Exists(ConfigPath)) BingeMigration.TryImport(BingeMigration.DefaultLegacyPath, ConfigPath);
        try
        {
            if (!File.Exists(ConfigPath)) return;
            // A scanner or indexer can hold the file for a moment at sign-in: a transient
            // read error is NOT a broken store. Retry a few times before failing closed.
            string? text = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                // A scanner, indexer or sync client can hold the file for a moment at
                // sign-in: a transient read OR permission error is NOT a broken store.
                // Retry both before failing closed.
                try { text = File.ReadAllText(ConfigPath); break; }
                catch (IOException) { if (attempt == 3) throw; Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { if (attempt == 3) throw; Thread.Sleep(250); }
            }
            using var doc = JsonDocument.Parse(text!);
            // Fail closed on ANY unreadable record (round-5, Main's catch): a dropped
            // entry would be permanently lost on the NEXT save, so one undecryptable
            // token marks the whole store broken - the file is preserved byte-for-byte,
            // reads look unpaired, no code is minted, and every save refuses.
            if (!TryParsePhones(doc.RootElement, out var parsedPhones, out var unreadableName))
            {
                StoreBroken = true;
                StoreBrokenReason = "could not be decrypted";
                Log("A saved phone" + (unreadableName != null ? " (\"" + unreadableName + "\")" : "")
                    + " could not be decrypted. The file is left untouched; pairing and saving are off. Reopen Zephyr VLC Remote to try again.");
                lock (PhonesLock) { Phones.Clear(); } DeclinedNetworks.Clear(); HomeNetworks.Clear(); MoviesFolder = null;
                return;
            }
            lock (PhonesLock)
            {
            Phones.Clear();
            Phones.AddRange(parsedPhones);
            DeclinedNetworks.Clear();
            HomeNetworks.Clear();
            if (doc.RootElement.TryGetProperty("declinedNetworks", out var dn))
                foreach (var n in dn.EnumerateArray())
                {
                    var v = n.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) DeclinedNetworks.Add(v);
                }
            if (doc.RootElement.TryGetProperty("homeNetworks", out var hn))
                foreach (var n in hn.EnumerateArray())
                {
                    var v = n.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) HomeNetworks.Add(v);
                }
            MoviesFolder = doc.RootElement.TryGetProperty("moviesFolder", out var mf) ? mf.GetString() : null;
            LastRunVersion = doc.RootElement.TryGetProperty("lastRunVersion", out var lv) ? lv.GetString() : null;
            var pvRaw = doc.RootElement.TryGetProperty("pendingVlcPassword", out var pvp) ? pvp.GetString() : null;
            var pvUnwrapped = pvRaw == null ? null : Dpapi.Unwrap(pvRaw);
            if (pvRaw != null && pvUnwrapped == null)
            {
                // Same fail-closed rule as a phone record: a dropped pending password
                // would silently erase the waiting vlcrc write on the next save.
                StoreBroken = true;
                StoreBrokenReason = "could not be decrypted";
                Log("The stored pending VLC password could not be decrypted. The file is left untouched; pairing and saving are off. Reopen Zephyr VLC Remote to try again.");
                lock (PhonesLock) { Phones.Clear(); } DeclinedNetworks.Clear(); HomeNetworks.Clear(); MoviesFolder = null;
                return;
            }
            _pendingVlcFromConfig = pvUnwrapped;
            }
        }
        catch (Exception ex)
        {
            StoreBroken = true;
            // Plain words on screen; the exception type stays in the log (round-8 N-8).
            StoreBrokenReason = ex is System.Text.Json.JsonException ? "is damaged" : "could not be read";
            lock (PhonesLock) { Phones.Clear(); } DeclinedNetworks.Clear(); HomeNetworks.Clear(); MoviesFolder = null;
            Log("Saved pairing data could not be read (" + ex.GetType().Name + "). The file is left untouched; pairing and saving are off. Reopen Zephyr VLC Remote to try again.");
        }
        // A vlcrc write that was waiting for VLC to close survives a helper restart:
        // complete it now when VLC is already closed, or re-arm the retry when it is not.
        var pendingVlc = _pendingVlcFromConfig;
        _pendingVlcFromConfig = null;
        if (!string.IsNullOrEmpty(pendingVlc)) VlcSetup.RestorePending(pendingVlc);
    }

    private static string? _pendingVlcFromConfig;

    /// <summary>One writer at a time: listener threads (pair, config, adopt, VLC setup)
    /// and the window thread all land here. This is the OUTER transaction lock: the whole
    /// read-modify-write (snapshot AND write) happens inside it, so two saves can never
    /// finish out of order and a stale snapshot can never overwrite a newer one. Lock
    /// order is ConfigSaveLock -> PhonesLock everywhere; nothing takes them the other way.
    /// Callers that mutate + persist wrap the whole transaction in this lock too.</summary>
    internal static readonly object ConfigSaveLock = new();

    internal static void SaveConfig()
    {
        // Round-5 B3: a file we failed to READ is never overwritten - the pairings it
        // holds are the only copy. Writes are atomic (temp file, then replace), so a
        // crash or power cut mid-save can never tear the file either.
        if (StoreBroken) throw new InvalidOperationException("Saved pairing data is unreadable; refusing to overwrite it.");
        lock (ConfigSaveLock)
        {
            string json;
            lock (PhonesLock)
            {
                json = JsonSerializer.Serialize(new
                {
                    phones = Phones.Select(p => new { token = Dpapi.Wrap(p.Token), name = p.Name, adoptSpent = p.AdoptSpent, adoptRequestId = p.AdoptRequestId, adoptServedAtUtc = p.AdoptServedAtUtc?.ToString("o") }).ToArray(),
                    pendingVlcPassword = VlcSetup.PendingValue is { } pv ? Dpapi.Wrap(pv) : null,
                    moviesFolder = MoviesFolder,
                    declinedNetworks = DeclinedNetworks.ToArray(),
                    homeNetworks = HomeNetworks.ToArray(),
                    lastRunVersion = LastRunVersion,
                });
            }
            Directory.CreateDirectory(DataDir);
            var tmp = ConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            try
            {
                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
                else File.Move(tmp, ConfigPath);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    /// <summary>Non-critical saves (Wi-Fi choices, version stamp): never crash the flow,
    /// always logged. Pairing and removal use SaveConfig directly so they can roll back.</summary>
    internal static bool TrySaveConfig(string what)
    {
        try { SaveConfig(); return true; }
        catch (Exception ex) { Log("Could not save " + what + ": " + ex.Message); return false; }
    }

    /// <summary>Transactional pairing (round-5 B3): the phone exists in memory only
    /// after it exists on disk. A failed save rolls the in-memory add back, so a
    /// refused write leaves no phantom pairing - and the caller keeps the code alive.</summary>
    internal static void AddPhoneAndPersist(string token, string? name)
    {
        // Whole transaction under the OUTER save lock (round-6): mutate + persist +
        // rollback are one serialized unit, so no concurrent save can persist a state
        // that this path is about to roll back.
        lock (ConfigSaveLock)
        {
            var phone = new PairedPhone { Token = token, Name = "" };
            lock (PhonesLock)
            {
                phone.Name = string.IsNullOrWhiteSpace(name) ? "Phone " + (Phones.Count + 1) : name!.Trim();
                Phones.Add(phone);
            }
            try { SaveConfig(); }
            catch { lock (PhonesLock) { Phones.Remove(phone); } throw; }
        }
    }

    internal static string MachineLabel() => Environment.MachineName;

    /// <summary>"1.3.1" - the running build, for the Updates card and feedback reports.</summary>
#if PAIR_CODE_HOOK
        // CI only: capture one of the modal dialogs exactly as constructed (same
        // DWM-composed PrintWindow path as the window shots).
        private static void CaptureDialogForShot(Form d, string path)
        {
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(0, 0);
            d.ShowInTaskbar = false;
            d.Show();
            d.Activate();
            Application.DoEvents();
            System.Threading.Thread.Sleep(400);
            Application.DoEvents();
            using (var bmp = new Bitmap(d.Width, d.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                bool captured = PrintWindowForShot(d.Handle, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!captured)
                {
                    g.Clear(d.BackColor);
                    d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height));
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            d.Close();
        }

        // Clicks the dialog button carrying the given DialogResult (the production
        // Cancel/confirm buttons), recursing through the layout rows.
        private static bool ClickDialogButton(Control root, DialogResult result)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Button b && b.DialogResult == result) { b.PerformClick(); return true; }
                if (ClickDialogButton(c, result)) return true;
            }
            return false;
        }

        // Runs the REAL ConfirmDialog.Ask while a timer clicks the requested button on
        // the shown modal dialog. Throws if no modal button matched (a silent miss
        // would read as a pass).
        private static bool AskWithAutoClick(string text, string confirmLabel, DialogResult click)
        {
            using var timer = new System.Windows.Forms.Timer { Interval = 400 };
            var clicked = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                foreach (Form f in Application.OpenForms)
                {
                    if (f.Modal) { clicked = ClickDialogButton(f, click); break; }
                }
            };
            timer.Start();
            var answer = ConfirmDialog.Ask(null, text, confirmLabel);
            if (!clicked) throw new InvalidOperationException("remove-paths: no modal dialog button matched " + click);
            return answer;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PrintWindow")]
        private static extern bool PrintWindowForShot(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
#endif

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? pwzCommandLine, int dwFlags);

    internal static string VersionText
    {
        get
        {
            var v = typeof(Program).Assembly.GetName().Version;
            return v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + v.Build;
        }
    }

    /// <summary>Background update check: on launch, and the window re-runs it daily.</summary>
    internal static void CheckForUpdate()
    {
        Task.Run(() =>
        {
            try
            {
                var current = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                var newer = UpdateCheck.LatestNewerThan(current);
                if (newer != null) Program.AvailableUpdate = newer.Value;
            }
            catch { /* a failed check is invisible */ }
        });
    }

    /// <summary>True when he turned Zephyr VLC Remote off in Settings > Startup apps (or Task Manager).
    /// Windows records it in StartupApproved; the watchdog honors it so off stays off.</summary>
    private static bool StartupDisabledByUser()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return k?.GetValue("Zephyr VLC Remote") is byte[] b && b.Length > 0 && (b[0] & 1) == 1; // 03/01 = disabled, 02 = enabled
        }
        catch { return false; }
    }

    /// <summary>A watchdog start that cannot listen used to fail silently every 5 minutes.
    /// Show one quiet tray balloon per sign-in instead; the marker file dedupes the repeats.</summary>
    private static void NotifyListenFailureOnce()
    {
        try
        {
            if (File.Exists(ListenFailNoticePath)) return;
            File.WriteAllText(ListenFailNoticePath, DateTime.UtcNow.ToString("O"));
            using var ni = new WinForms.NotifyIcon { Visible = true, BalloonTipTitle = "Zephyr VLC Remote" };
            try { ni.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
            catch { ni.Icon = System.Drawing.SystemIcons.Application; }
            ni.BalloonTipText = "Zephyr VLC Remote could not start listening for your phone. Open Zephyr VLC Remote from the Start menu to finish its setup.";
            ni.ShowBalloonTip(8000);
            Thread.Sleep(4000); // the balloon dies with the process; give it a moment to appear
        }
        catch { }
    }

    /// <summary>Start with Windows for this user (no admin needed).</summary>
    private static void RegisterAutostart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("ZephyrVlcRemote", false); // the 1.x entry name
            key?.SetValue("Zephyr VLC Remote", "\"" + Environment.ProcessPath + "\" --autostart");
        }
        catch { /* autostart is a convenience, not a requirement */ }
    }

    /// <summary>The tray Quit: remember it was on purpose so the watchdog does not revive the
    /// app until he starts it again or signs in.</summary>
    internal static void QuitOnPurpose()
    {
        try { File.WriteAllText(QuitMarkerPath, ""); } catch { }
        Log("Quit from the tray menu (staying off until the next sign-in or manual start).");
        Application.Exit();
    }

    /// <summary>A per-user scheduled task relaunches the app (silent --ensure-running) every
    /// 5 minutes when it is not running. Runs as him, so no admin prompt; uninstall deletes it.</summary>
    private static void RegisterWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            // XML, not schtasks flags: the defaults stop the task after 3 days, pause it on
            // battery and run it below normal priority - all wrong for keeping the app alive.
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n"
                + "  <Triggers><TimeTrigger><StartBoundary>2020-01-01T00:00:00</StartBoundary>"
                + "<Repetition><Interval>PT5M</Interval></Repetition></TimeTrigger></Triggers>\n"
                + "  <Settings>"
                + "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
                + "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"
                + "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"
                + "<StartWhenAvailable>true</StartWhenAvailable>"
                + "<Priority>4</Priority>"
                + "</Settings>\n"
                + "  <Actions><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command>"
                + "<Arguments>--ensure-running</Arguments></Exec></Actions>\n"
                + "</Task>";
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr-vlc-remote-watchdog.xml");
            System.IO.File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks",
                "/Create /F /TN \"Zephyr VLC Remote Watchdog\" /XML \"" + tmp + "\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(10000);
            try { System.IO.File.Delete(tmp); } catch { }
        }
        catch { /* the watchdog is a safety net, not a requirement */ }
    }
}


/// <summary>The helper window: the movies folder and pairing. Sits in the tray; closing it
/// only hides it. Follows the Windows dark/light mode setting, repainting live when it changes.</summary>
internal sealed class HelperWindow : Form
{
    // The app's palette: dark mode uses the app's colors, light mode their light counterparts.
    internal static readonly Color Orange = Color.FromArgb(255, 149, 0);
    internal static readonly Color OnOrange = Color.FromArgb(27, 27, 30);
    internal static readonly Color DarkBg = Color.FromArgb(27, 27, 30);
    internal static readonly Color DarkCard = Color.FromArgb(38, 38, 44);
    internal static readonly Color DarkCard2 = Color.FromArgb(46, 46, 54);
    internal static readonly Color DarkText = Color.FromArgb(235, 235, 240);
    internal static readonly Color DarkMuted = Color.FromArgb(150, 150, 160);
    // The subtle 1px edge on cards and the name box (Win11 settings-card look).
    internal static readonly Color DarkBorder = Color.FromArgb(56, 56, 64);

    private bool _allowShow;
    private bool _updateAnnounced;
    private bool _manualDownload;
    private readonly Label _pairHeader;
    private readonly TextBox _nameBox;
    private readonly RoundedPanel _nameBoxPanel;
    private readonly Button _pair;
    private readonly Label _codeCaption;
    private readonly Label _code;
    private readonly Label _codeNote;
    private readonly Button _cancelPair;
    private readonly Label _pairHint;
    private bool _codeExpired;
    private readonly Label _phonesHeader;
    private readonly FlowLayoutPanel _phoneList;
    private readonly Button _removeAll;
    private readonly Label _aboutHeader;
    private readonly RoundedPanel _feedbackCard;
    private readonly Button _feedbackBtn;
    private readonly RoundedPanel _updatesCard;
    private readonly Label _updatesCaption;
    private readonly Label _updatesResult;
    private readonly Button _checkUpdates;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ContextMenuStrip _menu;
    private readonly LinkLabel _firstRun;
    private readonly RoundedPanel _wifiBorder;
    private readonly Label _wifiNote;
    private readonly Button _wifiBtn;
    private NetProfile? _wifiProfile;
    private bool _wifiStripVisible;
    private bool _wifiDialogOpen;
    private readonly System.Windows.Forms.Timer? _wifiTimer;
    private readonly Dictionary<Control, int> _baseTops = new();
    private int _baseHeight;
    private bool _downloading;
    // Layout blocks that come and go: the first-run line (measured, it may wrap) and the
    // pairing code block (only while a code exists, i.e. after Pair is tapped).
    private readonly int _firstRunH;
    private readonly int _codeBlockH;
    private const int ListBaseH = 3 * 52; // the base layout reserves three rows
    private int _listH = ListBaseH;
    private bool _removeAllShown = true;
    private bool _firstRunShown;
    private bool _codeShown;

    public HelperWindow(bool windowShot = false)
    {
        // Scale every control, font and position with the screen's DPI, or fixed-pixel
        // layouts clip text and buttons on scaled displays.
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Zephyr VLC Remote";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        const int margin = 24;
        const int contentW = 490 - margin * 2;
        var y = margin;

        _pairHeader = Header("Pair new phone", margin, y); y += 20 + 12;
        _firstRun = new LinkLabel
        {
            Text = "New here? Get the Zephyr app on your phone, then give the phone a name and tap Pair.",
            Font = new Font("Segoe UI", 8f),
            AutoSize = true, Left = margin, Top = y,
            // Wrap inside the content width instead of running past the right margin.
            MaximumSize = new Size(contentW, 0),
            LinkColor = Orange, ActiveLinkColor = Orange, VisitedLinkColor = Orange,
        };
        // I3: the link covers "Zephyr app" and opens the release page, not the repo root.
        _firstRun.Links.Add(18, 10, "https://github.com/MtndaleRedneck/Zephyr-Dev/releases/latest");
        _firstRun.LinkClicked += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo((string)e.Link!.LinkData!) { UseShellExecute = true }); } catch { }
        };
        // Measured, not fixed: larger Windows text wraps the line to two rows.
        _firstRunH = TextRenderer.MeasureText(_firstRun.Text, _firstRun.Font,
            new Size(contentW, 1000), TextFormatFlags.WordBreak).Height + 6;
        y += _firstRunH;
        // One anti-aliased rounded frame, exactly the Pair button's top and height, with a
        // borderless TextBox centered inside it (no docking: a single-line TextBox keeps
        // its own height, which left a grey slab under it).
        // While Pair is greyed, a hint next to it says why; typing a name swaps it for the button.
        _pairHint = new Label
        {
            Text = "Type a name to pair",
            Font = new Font("Segoe UI", 8f),
            AutoSize = true, Top = y + 9,
        };
        var hintW = _pairHint.PreferredWidth + 10;
        _pairHint.Left = margin + contentW - 60 - 12 - hintW;
        _nameBoxPanel = new RoundedPanel
        {
            Left = margin, Top = y, Width = contentW - 60 - 12 - hintW, Height = 32,
            Radius = 4, Cursor = Cursors.IBeam,
        };
        _nameBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10),
            PlaceholderText = "Phone name",
        };
        _nameBoxPanel.Controls.Add(_nameBox);
        _nameBoxPanel.Layout += (_, _) =>
        {
            const int padX = 10;
            _nameBox.SetBounds(padX, (_nameBoxPanel.ClientSize.Height - _nameBox.Height) / 2,
                Math.Max(10, _nameBoxPanel.ClientSize.Width - padX * 2), _nameBox.Height);
        };
        _nameBoxPanel.Click += (_, _) => _nameBox.Focus();
        _pair = MakeButton("Pair", margin + contentW - 60, y, 60);
        ((ReadableButton)_pair).DimWhenDisabled = false; // StylePairEnabled owns the disabled look
        _pair.Height = 32;
        _pair.Enabled = false;
        StylePairEnabled();
        _pair.EnabledChanged += (_, _) => StylePairEnabled();
        _pair.Click += (_, _) => StartPairing();
        _nameBox.TextChanged += (_, _) =>
        {
            _pair.Enabled = _nameBox.Text.Trim().Length > 0;
            _pairHint.Visible = !_pair.Enabled;
        };
        y += 32 + 12;
        _codeCaption = new Label
        {
            Text = "Pairing code:",
            Font = new Font("Segoe UI", 9),
            AutoSize = true, Left = margin, Top = y + 8,
            Visible = false,
        };
        _code = new Label
        {
            Text = Program.PairCode ?? "------",
            Font = new Font("Consolas", 15, FontStyle.Bold),
            ForeColor = Orange,
            AutoSize = true, Left = margin + 98, Top = y,
            Visible = false,
        };
        _cancelPair = MakeButton("Cancel", margin + contentW - 80, y - 2, 80);
        _cancelPair.Visible = false;
        _cancelPair.Click += (_, _) => CancelPairing();
        y += 28 + 6;
        _codeNote = new Label
        {
            Text = "Enter this code in the Zephyr app on your phone. Works once, for 10 minutes.",
            Font = new Font("Segoe UI", 8),
            AutoSize = false, Left = margin, Top = y, Width = contentW,
            Visible = false,
        };
        // Measure, don't guess: larger Windows text wraps this to three lines.
        _codeNote.Height = TextRenderer.MeasureText(_codeNote.Text, _codeNote.Font,
            new Size(contentW, 1000), TextFormatFlags.WordBreak).Height + 4;
        y += _codeNote.Height + 24;
        // The code block (caption, code, note) shows only while a code exists; without it
        // "Paired phones" sits 24px under the name row, like every other section gap.
        _codeBlockH = 12 + 28 + 6 + _codeNote.Height;

        _phonesHeader = Header("Paired phones", margin, y); y += 20 + 12;
        _phoneList = new FlowLayoutPanel
        {
            Left = margin, Top = y, Width = contentW, Height = 3 * 52,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        y += 3 * 52 + 8;
        _removeAll = MakeButton("Remove all phones...", margin, y, FitWidth("Remove all phones...", new Font("Segoe UI", 8.5f), 150));
        _removeAll.Height = 32;
        _removeAll.Click += (_, _) => RemoveAllPhones();
        y += 32 + 24;

        _aboutHeader = Header("About", margin, y); y += 20 + 12;
        _feedbackCard = new RoundedPanel { Left = margin, Top = y, Width = contentW, Height = 48, Radius = 8 };
        var feedbackLabel = new Label
        {
            Text = "Feedback & bugs",
            Font = new Font("Segoe UI", 10.5f),
            AutoSize = true, Left = 14,
            UseMnemonic = false,
        };
        var fbw = FitWidth("Send feedback", new Font("Segoe UI", 8.5f), 130);
        _feedbackBtn = MakeButton("Send feedback", contentW - fbw - 10, 9, fbw);
        _feedbackBtn.Height = 30;
        CenterChildrenVertically(_feedbackCard);
        _feedbackBtn.Click += (_, _) => { using var dlg = new FeedbackDialog(); dlg.ShowDialog(this); };
        _feedbackCard.Controls.Add(feedbackLabel);
        _feedbackCard.Controls.Add(_feedbackBtn);
        y += 48 + 4;
        _updatesCard = new RoundedPanel { Left = margin, Top = y, Width = contentW, Height = 48, Radius = 8 };
        var updatesLabel = new Label
        {
            Text = "Updates",
            Font = new Font("Segoe UI", 10.5f),
            AutoSize = true, Left = 14,
        };
        _updatesCaption = new Label
        {
            Text = "v" + Program.VersionText,
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = true,
            // Right after the label, however wide larger Windows text makes it.
            Left = 14 + TextRenderer.MeasureText("Updates", new Font("Segoe UI", 10.5f)).Width + 10,
        };
        _updatesResult = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = false,
            Left = _updatesCaption.Left + TextRenderer.MeasureText(_updatesCaption.Text, _updatesCaption.Font).Width + 8,
            AutoEllipsis = true,
        };
        _updatesResult.Height = _updatesResult.PreferredHeight;
        var cuw = FitWidth("Check for updates", new Font("Segoe UI", 8.5f), 140);
        _updatesResult.Width = contentW - cuw - 10 - _updatesResult.Left - 10;
        _checkUpdates = MakeButton("Check for updates", contentW - cuw - 10, 9, cuw);
        _checkUpdates.Height = 30;
        _checkUpdates.Click += (_, _) => ManualUpdateCheck();
        CenterChildrenVertically(_updatesCard);
        _updatesCard.Controls.Add(updatesLabel);
        _updatesCard.Controls.Add(_updatesCaption);
        _updatesCard.Controls.Add(_updatesResult);
        _updatesCard.Controls.Add(_checkUpdates);
        y += 48 + margin;

        ClientSize = new Size(490, y);

        // The public-Wi-Fi strip: sits at the top of the window while the network is
        // public, so the fix is never buried. The 30s poll keeps it in sync.
        // One anti-aliased card with a 1px orange edge (it used to be an orange panel with a
        // card clipped inside it, both cut by pixel regions).
        _wifiBorder = new RoundedPanel
        {
            Left = margin, Top = margin, Width = contentW, Height = 62,
            Radius = 8, BorderColor = Orange, Visible = false,
        };
        var wifiText = new Label
        {
            Text = "Windows marks this Wi-Fi as public, which can block your phone.",
            Font = new Font("Segoe UI", 9f),
            Left = 13, Top = 9, Width = contentW - 12 - 160 - 16, Height = 62 - 16,
        };
        _wifiBtn = MakeButton("This is my home Wi-Fi", contentW - 160 - 10 + 1, 7 + 1, 160);
        _wifiBtn.Height = 28;
        StylePrimary(_wifiBtn);
        _wifiBtn.Click += (_, _) => FixHomeWifi();
        _wifiNote = new Label
        {
            Text = "(Windows will ask for permission)",
            Font = new Font("Segoe UI", 7.5f),
            Width = 160, TextAlign = ContentAlignment.TopCenter,
            Left = contentW - 160 - 10 + 1, Top = 40 + 1, Height = 16,
        };
        _wifiBorder.Controls.Add(wifiText);
        _wifiBorder.Controls.Add(_wifiBtn);
        _wifiBorder.Controls.Add(_wifiNote);

        Controls.AddRange(new Control[]
        {
            _wifiBorder,
            _pairHeader, _firstRun, _nameBoxPanel, _pairHint, _pair, _codeCaption, _code, _cancelPair, _codeNote,
            _phonesHeader, _phoneList, _removeAll,
            _aboutHeader, _feedbackCard, _updatesCard,
        });
        foreach (Control c in Controls) _baseTops[c] = c.Top;
        _baseHeight = y;

        _menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };
        _menu.Items.Add("Show Zephyr VLC Remote", null, (_, _) => ShowFromTray());
        _menu.Items.Add("Quit Zephyr VLC Remote", null, (_, _) => Program.QuitOnPurpose());
        _tray = new NotifyIcon
        {
            Text = "Zephyr VLC Remote",
            Icon = Icon,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => { ShowFromTray(); StartUpdateDownload(); };

        // The server adds and drops phones off-thread; repaint the list when that happens.
        Program.PhonesChanged = () =>
        {
            try { BeginInvoke(new Action(() => { RefreshPhones(); TickStatus(); })); } catch { }
        };

        // Public Wi-Fi silently blocks the phone: an in-window strip with the one-click fix
        // stays up while the network is public, and the popup still asks at launch and on
        // network changes (at most once per Wi-Fi name, except home networks, which re-ask).
        Action<NetProfile> showWifi = p =>
        {
            try { BeginInvoke(new Action(() => { _wifiProfile = p; SetWifiStripVisible(true); ShowPublicWifiDialog(p); })); } catch { }
        };
        if (windowShot)
        {
            _wifiProfile = new NetProfile { Name = "Home Wi-Fi", Category = "Public" };
            SetWifiStripVisible(true);
        }
        else
        Task.Run(() => { try { NetworkCheck.CheckNow(showWifi); } catch { /* never stop startup */ } });
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            try { NetworkCheck.QueueCheck(showWifi); } catch { }
        };
        // A profile flipped to Private in Windows Settings raises no network event, so the
        // strip re-checks every 30 seconds. The same poll re-asks for a home network.
        if (!windowShot)
        {
            _wifiTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _wifiTimer.Tick += (_, _) =>
            {
                // The 30s re-check exists for the strip/dialog; no PowerShell while nothing
                // Wi-Fi-related is on screen. Network changes raise their own event.
                if (Visible || _wifiProfile != null || _wifiDialogOpen) ProbeWifi();
            };
            _wifiTimer.Start();
        }

        // Follow the Windows dark/light mode setting, live.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            try { BeginInvoke(new Action(ApplyTheme)); } catch { }
        };
        ApplyTheme();
        RefreshPhones();
        TickStatus(); // sync the code block (hidden until there is a code)

        // A vlcrc write that lost to a running VLC retries once VLC closes.
        var vlcrcRetry = new System.Windows.Forms.Timer { Interval = 15000 };
        vlcrcRetry.Tick += (_, _) => { try { VlcSetup.RetryPending(); } catch { } };
        vlcrcRetry.Start();

        // Daily re-check for updates while running.
        var daily = new System.Windows.Forms.Timer { Interval = 24 * 60 * 60 * 1000 };
        daily.Tick += (_, _) => { Program.CheckForUpdate(); AnnounceUpdateIfNeeded(); };
        daily.Start();
        // The startup update check finishes on its own task; announce it once it lands.
        Task.Run(async () =>
        {
            await Task.Delay(20000);
            try { BeginInvoke(new Action(AnnounceUpdateIfNeeded)); } catch { }
        });

        // The pairing-code countdown only matters while the window is on screen, so this
        // timer sleeps whenever the window is tucked in the tray: with the window hidden the
        // helper wakes only for the phone, the network check, or the daily update check.
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => TickStatus();
        // First launch with the window already on screen (unpaired PCs): start ticking now.
        if (Visible) { TickStatus(); _timer.Start(); }
    }

    private const int StripH = 74;

    /// <summary>Base layout + the optional blocks (Wi-Fi strip on top, first-run line
    /// under the pair header, pairing code under the name row): everything below a
    /// hidden block moves up. Uses our own flags, not Control.Visible, which reads false
    /// for every child while the window itself is hidden.</summary>
    private void ApplyLayout()
    {
        if (_baseHeight == 0 || _firstRun == null || _wifiBorder == null) return;
        var stripDy = _wifiStripVisible ? StripH : 0;
        var firstRunDy = _firstRunShown ? 0 : -_firstRunH;
        var codeDy = _codeShown ? 0 : -_codeBlockH;
        // The phone list is as tall as its rows (one line when empty, three rows at most,
        // then it scrolls), and with no phones "Remove all phones..." leaves no gap behind.
        var listDy = _listH - ListBaseH;
        var removeDy = _removeAllShown ? 0 : -(8 + _removeAll.Height);
        var frTop = _baseTops[_firstRun];
        var codeEnd = _baseTops[_codeNote];
        var listTop = _baseTops[_phoneList];
        var removeTop = _baseTops[_removeAll];
        foreach (Control c in Controls)
        {
            if (c == _wifiBorder) { c.Top = _baseTops[c]; continue; }
            var dy = stripDy;
            if (_baseTops[c] > frTop) dy += firstRunDy;
            if (_baseTops[c] > codeEnd) dy += codeDy;
            if (_baseTops[c] > listTop) dy += listDy;
            if (_baseTops[c] > removeTop) dy += removeDy;
            c.Top = _baseTops[c] + dy;
        }
        _phoneList.Height = _listH;
        ClientSize = new Size(490, _baseHeight + stripDy + firstRunDy + codeDy + listDy + removeDy);
    }

    private void SetWifiStripVisible(bool show)
    {
        if (show == _wifiStripVisible) return;
        _wifiStripVisible = show;
        _wifiBorder.Visible = show;
        ApplyLayout();
    }

    /// <summary>Which home network already got its one re-ask for this Public flip; cleared
    /// when the network is Private again.</summary>
    private string? _homeReasked;

    /// <summary>The 30-second profile re-check: sync the strip with reality (a network fixed
    /// in Windows Settings raises no event). The popup itself belongs to launch and real
    /// network changes; the only popup from the poll is one re-ask per Public flip for a
    /// network marked home, which "Not now" must not bury.</summary>
    private void ProbeWifi()
    {
        if (_wifiDialogOpen) return;
        Task.Run(() =>
        {
            var p = NetworkCheck.DefaultRouteProfile();
            var isPublic = p != null && p.Category.Equals("Public", StringComparison.OrdinalIgnoreCase);
            try { BeginInvoke(new Action(() =>
            {
                _wifiProfile = isPublic ? p : null;
                SetWifiStripVisible(isPublic);
                if (!isPublic || p == null) { _homeReasked = null; return; }
                var knownHome = Program.HomeNetworks.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
                if (knownHome && _homeReasked != p.Name)
                {
                    _homeReasked = p.Name;
                    ShowPublicWifiDialog(p);
                }
            })); } catch { }
        });
    }

    /// <summary>The strip's one-click fix: mark this Wi-Fi Private (one elevated Windows prompt).</summary>
    private void FixHomeWifi()
    {
        var profile = _wifiProfile;
        if (profile == null) return;
        _wifiBtn.Enabled = false;
        Task.Run(() =>
        {
            var ok = NetworkCheck.MarkPrivate(profile);
            try { BeginInvoke(new Action(() =>
            {
                _wifiBtn.Enabled = true;
                if (ok)
                {
                    // The Wi-Fi lists are persisted state: mutate + save inside the
                    // outer save lock, or a listener thread's SaveConfig can enumerate
                    // them mid-change and fail an unrelated save (round-7 N3).
                    lock (Program.ConfigSaveLock)
                    {
                        Program.DeclinedNetworks.RemoveAll(n => string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase));
                        if (!Program.HomeNetworks.Any(n => string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase)))
                            Program.HomeNetworks.Add(profile.Name);
                        Program.TrySaveConfig("the Wi-Fi choice");
                    }
                    Program.Log("Network \"" + profile.Name + "\" marked Private from the window strip.");
                    _wifiProfile = null;
                    SetWifiStripVisible(false);
                    NoteDialog.Show(this,
                        "\u2713 Done. Your phone can reach this PC now.\n\nThis Wi-Fi is now marked Private in Windows, and Windows remembers it.");
                }
                else
                {
                    NoteDialog.Show(this,
                        "The change did not go through. Opening the Windows setting so you can switch this Wi-Fi to Private by hand.");
                    OpenWifiSettings();
                }
            })); } catch { }
        });
    }

    /// <summary>CI-only: show the window offscreen, let layout settle, and save a PNG of it.
    /// DrawToBitmap renders in memory, so no visible desktop is needed.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

#if WINDOW_SHOT_HOOK
    /// <summary>CI-only: run one status tick so the shot reflects settled pairing state.</summary>
    internal void RunStatusTickForShot() { TickStatus(); }

    // CI only: the tray menu rendered by its real dark renderer, captured off the
    // desktop at its shown bounds (round-9 missing captures).
    internal void ShowTrayMenuForShot(string path)
    {
        _menu.Show(new Point(60, 320));
        Application.DoEvents();
        System.Threading.Thread.Sleep(350);
        Application.DoEvents();
        var b = _menu.Bounds;
        using (var bmp = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height)))
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        _menu.Close();
    }
#endif

    internal void ShowOffscreenAndCapture(string path)
    {
        // Shown at (0,0): offscreen coordinates make DWM skip composing the window,
        // which is what dropped the placeholder and link text in earlier captures.
        StartPosition = FormStartPosition.Manual;
        Location = new Point(0, 0);
        ShowInTaskbar = false;
        Show();
        Activate();
        Application.DoEvents();
        System.Threading.Thread.Sleep(600);
        // Re-assert the final first-run state and re-run layout right before the shot:
        // whatever order the strip/phones events landed in, this is the settled state
        // a real desktop reaches after OnLoad.
        _firstRun.Visible = Program.PhonesCount == 0;
        ApplyLayout();
        Application.DoEvents();
        // Capture 1: DWM-composed (PW_RENDERFULLCONTENT) so cue banners and
        // LinkLabel text that WM_PRINT (DrawToBitmap) drops still show.
        using (var bmp = new Bitmap(Width, Height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool captured = PrintWindow(Handle, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!captured)
                {
                    g.Clear(BackColor);
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                }
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        // Diagnostics: is the Pair row really collapsed, or is the capture lying?
        try
        {
            var diagPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(path) ?? ".", "window-diag.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("DeviceDpi=" + DeviceDpi);
            foreach (var kv in _baseTops) sb.AppendLine("base " + kv.Key.Name + "=" + kv.Value);
            sb.AppendLine("AutoScaleMode=" + AutoScaleMode + " AutoScaleDimensions=" + CurrentAutoScaleDimensions + " (saved " + AutoScaleDimensions + ")");
            foreach (var (n, c) in new (string, Control)[] {
                ("firstRun", _firstRun), ("nameBox", _nameBox), ("pair", _pair),
                ("codeCaption", _codeCaption), ("wifiBorder", _wifiBorder) })
            {
                string region;
                try { using var g = Graphics.FromHwnd(IntPtr.Zero);
                      var r = c.Region?.GetBounds(g) ?? System.Drawing.RectangleF.Empty;
                      region = r.Width + "x" + r.Height; }
                catch { region = "?"; }
                sb.AppendLine(n + " bounds=" + c.Bounds + " region=" + region + " visible=" + c.Visible);
            }
            System.IO.File.WriteAllText(diagPath, sb.ToString());
        }
        catch { }
        // Capture 2: what the user would see on the desktop - ONLY when the window is
        // really on a screen. The shot window sits OFFscreen, where CopyFromScreen
        // would grab unrelated desktop pixels (round-9 N9-11: two "different" states
        // produced byte-identical -screen.png files that showed neither state). No
        // misleading artifact is better than a mislabeled one.
        try
        {
            var bounds = new System.Drawing.Rectangle(Left, Top, Width, Height);
            var onScreen = false;
            foreach (var scr in System.Windows.Forms.Screen.AllScreens)
                if (scr.Bounds.IntersectsWith(bounds)) { onScreen = true; break; }
            if (onScreen)
            {
                var screenPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(path) ?? ".",
                    System.IO.Path.GetFileNameWithoutExtension(path) + "-screen.png");
                using var bmp2 = new Bitmap(Width, Height);
                using var g2 = Graphics.FromImage(bmp2);
                g2.CopyFromScreen(Left, Top, 0, 0, new Size(Width, Height));
                bmp2.Save(screenPath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch { }
        Close();
    }

    /// <summary>Roll an expired pairing code (window-visible work).</summary>
    private bool _brokenShown;

    private void TickStatus()
    {
        if (Program.StoreBroken)
        {
            // Broken store (round-5): plainly say the saved pairings could not be loaded
            // instead of inviting a fresh pair - a pair here could never persist, and the
            // existing file is being preserved, not reset.
            if (!_brokenShown)
            {
                _brokenShown = true;
                _codeShown = true;
                _codeCaption.Visible = _code.Visible = _cancelPair.Visible = false;
                _codeNote.Visible = true;
                _codeNote.Text = "The saved pairings on this PC " + Program.StoreBrokenReason
                    + ". Pairing and every connected phone are off, and nothing will be saved over the file - it is kept exactly as it was at "
                    + Program.ConfigPath + ". Reopen Zephyr VLC Remote to try again.";
                _pair.Enabled = false;
                _nameBox.Enabled = false;
                // The note is measured for its short default text; this longer message
                // needs real room or it paints over the phone list. Grow the note and
                // shift everything below it (the window grows with it).
                var noteW = ClientSize.Width - 48;
                var noteH = TextRenderer.MeasureText(_codeNote.Text, _codeNote.Font,
                    new Size(noteW, 1000), TextFormatFlags.WordBreak).Height + 4;
                if (noteH != _codeNote.Height)
                {
                    var delta = noteH - _codeNote.Height;
                    var noteTop = _baseTops[_codeNote];
                    _codeNote.Height = noteH;
                    foreach (var c in _baseTops.Keys.ToList())
                        if (_baseTops[c] > noteTop) _baseTops[c] += delta;
                    _baseHeight += delta;
                }
                ApplyLayout();
            }
            return;
        }
        // An expired code STAYS SET, only flagged: the phone must hear the truth -
        // 410 "code expired", never a misleading "wrong code" (round-5). Nulling the
        // code here raced the HTTP expiry check: a /pair landing after the sweep got
        // 403 "no code showing" instead of 410 (first expired-code CI run).
        var expiredNow = Program.PairCode != null && DateTime.UtcNow > Program.PairCodeExpires;
        if (expiredNow && !_codeExpired)
        {
            // The typed name goes back into the box (Pair re-enables) and the note says
            // what happened; the code itself is unusable (410) but stays for the answer.
            string? name;
            lock (HttpServer.PairLock)
            {
                name = Program.PendingPhoneName;
                Program.PendingPhoneName = null;
            }
            if (!string.IsNullOrEmpty(name) && _nameBox.Text.Trim().Length == 0) _nameBox.Text = name;
            _codeExpired = true;
            Program.Log("Pairing code expired - showing \"Code expired\" until Pair is tapped again.");
        }
        if (!expiredNow) _codeExpired = false;
        _code.Text = expiredNow ? "------" : Program.PairCode ?? "------";
        // The block shows a live code (caption, code, Cancel, note) or the expired note alone.
        var live = Program.PairCode != null && !expiredNow;
        var show = live || _codeExpired;
        if (show != _codeShown || live != _code.Visible)
        {
            _codeShown = show;
            _codeCaption.Visible = _code.Visible = _cancelPair.Visible = live;
            _codeNote.Visible = show;
            _codeNote.Text = live
                ? "Enter this code in the Zephyr app on your phone. Works once, for 10 minutes."
                : "Code expired. Tap Pair for a new one.";
            ApplyLayout();
        }
    }

    /// <summary>Cancel drops the code and goes back to step one, keeping the typed name.</summary>
    private void CancelPairing()
    {
        string? name;
        lock (HttpServer.PairLock)
        {
            name = Program.PendingPhoneName;
            Program.PairCode = null;
            Program.PendingPhoneName = null;
        }
        if (!string.IsNullOrEmpty(name) && _nameBox.Text.Trim().Length == 0) _nameBox.Text = name;
        _codeExpired = false;
        Program.Log("Pairing canceled from the window.");
        TickStatus();
    }

    /// <summary>Rebuild the paired-phones list from the current config. Each phone is one
    /// settings-style card: name on the left, Remove inside at the right.</summary>
    private void RefreshPhones()
    {
        var card = DarkCard;
        var btnBg = DarkCard2;
        var text = DarkText;
        var muted = DarkMuted;
        var border = DarkBorder;
        _phoneList.Controls.Clear();
        var rowWidth = Program.PhonesCount > 3 ? 442 - 17 : 442; // leave room for the scroll bar
        if (Program.PhonesCount == 0)
        {
            var empty = new Label
            {
                Text = "No phones paired yet.",
                Font = new Font("Segoe UI", 9f),
                AutoSize = true, Margin = new Padding(2, 2, 0, 0),
                ForeColor = muted, BackColor = _phoneList.BackColor,
            };
            _phoneList.Controls.Add(empty);
            _listH = empty.PreferredHeight + 4;
        }
        else
        {
            // Each row is 48 tall plus a 4px gap; past three rows the list scrolls.
            _listH = Math.Min(Program.PhonesCount, 3) * 52;
        }
        foreach (var ph in Program.PhonesSnapshot())
        {
            var row = new RoundedPanel
            {
                Width = rowWidth, Height = 48, Margin = new Padding(0, 0, 0, 4),
                BackColor = card, BorderColor = border, Radius = 8,
            };
            var removeFont = new Font("Segoe UI", 8.5f);
            var removeW = FitWidth("Remove", removeFont, 64);
            var name = new Label
            {
                Text = ph.Name,
                AutoEllipsis = true, AutoSize = false,
                Left = 14, Width = rowWidth - 14 - removeW - 10 - 10,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = text, BackColor = card,
            };
            name.Height = name.PreferredHeight + 4;
            var remove = new ReadableButton
            {
                Text = "Remove", Width = removeW, Height = 30, Left = rowWidth - removeW - 10,
                Font = removeFont,
                BackColor = btnBg, ForeColor = text,
            };
            var phone = ph;
            remove.Click += (_, _) => RemovePhone(phone);
            row.Controls.Add(name);
            row.Controls.Add(remove);
            CenterChildrenVertically(row);
            _phoneList.Controls.Add(row);
        }
        _removeAllShown = Program.PhonesCount > 0;
        _removeAll.Visible = _removeAllShown;
        _firstRunShown = Program.PhonesCount == 0;
        _firstRun.Visible = _firstRunShown;
        ApplyLayout();
    }

    private void RemovePhone(Program.PairedPhone ph)
    {
        if (!ConfirmDialog.Ask(this,
                "Remove " + ph.Name + "? It stops controlling this PC through Zephyr VLC Remote right away. " +
                "One thing stays: VLC's own remote (port 8080) uses one shared password, so this phone could still reach VLC directly until the VLC password changes. " +
                "You can pair the phone again any time with a new code.",
                "Remove")) return;
        bool removedOk = false;
        lock (Program.ConfigSaveLock)
        {
            lock (Program.PhonesLock) { Program.Phones.Remove(ph); }
            try { Program.SaveConfig(); removedOk = true; }
            catch (Exception ex)
            {
                // Transactional (B3): a refused save rolls the removal back - no phantom.
                lock (Program.PhonesLock) { Program.Phones.Add(ph); }
                Program.Log("Phone removal could not be saved: " + ex.Message);
            }
        }
        if (!removedOk) { RefreshPhones(); return; }
        Program.Log("Phone removed: " + ph.Name + " (" + Program.PhonesCount + " left).");
        RefreshPhones();
    }

    private void RemoveAllPhones()
    {
        if (Program.PhonesCount == 0) return;
        if (!ConfirmDialog.Ask(this,
                "Remove all phones? Every phone stops controlling this PC through Zephyr VLC Remote right away. " +
                "One thing stays: VLC's own remote (port 8080) uses one shared password, so a phone could still reach VLC directly until the VLC password changes. " +
                "Pair them again with new codes.",
                "Remove all")) return;
        Program.PairedPhone[] backup;
        bool clearedOk = false;
        lock (Program.ConfigSaveLock)
        {
            lock (Program.PhonesLock) { backup = Program.Phones.ToArray(); Program.Phones.Clear(); }
            try { Program.SaveConfig(); clearedOk = true; }
            catch (Exception ex)
            {
                lock (Program.PhonesLock) { Program.Phones.Clear(); Program.Phones.AddRange(backup); }
                Program.Log("Removing all phones could not be saved: " + ex.Message);
            }
        }
        if (!clearedOk) { RefreshPhones(); return; }
        Program.Log("All phones removed.");
        RefreshPhones();
    }

    /// <summary>Type the phone's name, tap Pair, and the code for it appears below.</summary>
    private void StartPairing()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0) return;
        if (Program.StoreBroken)
        {
            Program.Log("Pairing refused: the saved pairing data on this PC could not be read (the file is preserved).");
            return;
        }
        // The HTTP pair handler reads this state: mutate it under the same lock so a
        // /pair can never observe a half-set code (round-7 review C).
        lock (HttpServer.PairLock)
        {
            Program.PendingPhoneName = name;
            Program.PairCode = Program.NewPairCode();
            Program.PairCodeExpires = DateTime.UtcNow + Program.PairCodeLifetime;
            Program.PairFailures = 0;
        }
        _nameBox.Clear();
        _codeExpired = false;
        Program.Log("Pairing code created for \"" + name + "\".");
        TickStatus();
    }

    private void AnnounceUpdateIfNeeded()
    {
        if (Program.AvailableUpdate == null || _updateAnnounced) return;
        _updateAnnounced = true;
        var v = Program.AvailableUpdate.Value.Version;
        _menu.Items.Insert(0, new ToolStripMenuItem("Download update v" + v, null, (_, _) => { ShowFromTray(); StartUpdateDownload(); }));
        _tray.BalloonTipTitle = "Zephyr VLC Remote update";
        _tray.BalloonTipText = "Version " + v + " is available. Click here to download it.";
        _tray.ShowBalloonTip(8000);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_timer == null) return;
        if (Visible) { TickStatus(); AnnounceUpdateIfNeeded(); _timer.Start(); }
        else _timer.Stop();
    }

    /// <summary>Width that fits the text at any Windows text size: measured, plus air.</summary>
    private static int FitWidth(string text, Font font, int min)
    {
        var w = TextRenderer.MeasureText(text, font).Width + 28;
        return Math.Max(min, w);
    }

    private static Button MakeButton(string text, int left, int top, int width) => new ReadableButton
    {
        Text = text,
        Left = left, Top = top, Width = width, Height = 30,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 8.5f),
    };

    private void ApplyTheme()
    {
        var bg = DarkBg;
        var card = DarkCard;
        var btnBg = DarkCard;
        var cardBtn = DarkCard2;
        var text = DarkText;
        var muted = DarkMuted;
        var border = DarkBorder;
        BackColor = bg;
        ForeColor = text;
        _pairHeader.ForeColor = text;
        _phonesHeader.ForeColor = text;
        _aboutHeader.ForeColor = text;
        _nameBoxPanel.BackColor = card;
        _nameBoxPanel.BorderColor = border;
        _nameBox.BackColor = card;
        _nameBox.ForeColor = text;
        _codeCaption.ForeColor = text;
        _code.ForeColor = Orange;
        _codeNote.ForeColor = text;
        _pairHint.ForeColor = muted;
        _pairHint.BackColor = bg;
        _phoneList.BackColor = bg;
        _firstRun.BackColor = bg;
        _firstRun.ForeColor = text;
        _firstRun.LinkColor = Orange;
        _firstRun.ActiveLinkColor = Orange;
        _firstRun.VisitedLinkColor = Orange;
        _wifiBorder.BackColor = card;
        _wifiBorder.BorderColor = Orange;
        foreach (Control c in _wifiBorder.Controls)
        {
            if (c is Button) continue;
            c.BackColor = card;
            c.ForeColor = text;
        }
        _feedbackCard.BackColor = card;
        _updatesCard.BackColor = card;
        _feedbackCard.BorderColor = border;
        _updatesCard.BorderColor = border;
        foreach (Control c in _feedbackCard.Controls) { c.BackColor = c is Button ? cardBtn : card; c.ForeColor = text; }
        foreach (Control c in _updatesCard.Controls) { if (c is Button) continue; c.BackColor = card; c.ForeColor = text; }
        _updatesCaption.ForeColor = muted;
        _feedbackBtn.FlatAppearance.BorderSize = 0;
        _checkUpdates.FlatAppearance.BorderSize = 0;
        foreach (var b in new[] { _removeAll, _cancelPair })
        {
            b.BackColor = cardBtn;
            b.ForeColor = text;
            b.FlatAppearance.BorderSize = 0;
        }
        StylePairEnabled();
        if (_manualDownload) StylePrimary(_checkUpdates); else { _checkUpdates.BackColor = cardBtn; _checkUpdates.ForeColor = text; }
        RefreshPhones();
        Invalidate(true); // rounded corners blend into the new background
        ApplyTitleBarTheme();
    }

    /// <summary>Dark title bar for the standalone dialogs (Wi-Fi popup, confirm, note).</summary>
    internal static void ApplyDarkTitleBar(Form f)
    {
        f.HandleCreated += (_, _) =>
        {
            var dark = 1;
            try { DwmSetWindowAttribute(f.Handle, 20, ref dark, 4); } catch { /* older Windows */ }
        };
    }

    /// <summary>The window's title bar follows the Windows dark/light setting (immersive dark mode).</summary>
    internal void ApplyTitleBarTheme()
    {
        if (!IsHandleCreated) return;
        var dark = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { /* older Windows */ }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Win11 corner radii: 4px on buttons and boxes, 8px on cards.</summary>
    internal static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Paint a smooth (anti-aliased) rounded shape: the corners outside it take
    /// the parent's color, the inside is filled, plus an optional 1px edge. Replaces the
    /// old Control.Region rounding, which is a hard pixel mask (jagged, never smoothed).
    /// Radius is in the same pixels as the layout: the window's positions are fixed
    /// pixels, so the corners stay in proportion with the boxes.</summary>
    internal static void PaintRounded(Graphics g, Control c, Color fill, Color border, int radius)
    {
        g.Clear(c.Parent?.BackColor ?? SystemColors.Control);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        using (var path = RoundedRect(new RectangleF(0, 0, c.Width, c.Height), radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        if (!border.IsEmpty && border.A > 0)
        {
            using var edge = RoundedRect(new RectangleF(0.5f, 0.5f, c.Width - 1, c.Height - 1), Math.Max(0, radius - 0.5f));
            using var pen = new Pen(border, 1f);
            g.DrawPath(pen, edge);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default;
    }

    /// <summary>Keep every child of a card vertically centered by measurement, at any DPI
    /// or Windows text size (fixed Tops put "Feedback & bugs" low when the text grew).</summary>
    internal static void CenterChildrenVertically(Control card)
    {
        var busy = false;
        void Center()
        {
            if (busy) return;
            busy = true;
            try
            {
                foreach (Control c in card.Controls)
                {
                    var h = c is Label { AutoSize: true } l ? l.PreferredHeight : c.Height;
                    var top = (card.ClientSize.Height - h) / 2;
                    if (c.Top != top) c.Top = top;
                }
            }
            finally { busy = false; }
        }
        card.Layout += (_, _) => Center();
        card.Resize += (_, _) => Center();
        card.ControlAdded += (_, _) => Center();
        Center();
    }

    /// <summary>A panel drawn as a smooth rounded card: BackColor inside, the parent's
    /// color in the corners, an optional 1px BorderColor edge.</summary>
    internal sealed class RoundedPanel : Panel
    {
        private int _radius = 8;
        private Color _borderColor = Color.Empty;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Radius { get => _radius; set { _radius = value; Invalidate(); } }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color BorderColor { get => _borderColor; set { _borderColor = value; Invalidate(); } }

        protected override void OnPaintBackground(PaintEventArgs e) =>
            PaintRounded(e.Graphics, this, BackColor, _borderColor, _radius);

        protected override void OnParentBackColorChanged(EventArgs e)
        {
            base.OnParentBackColorChanged(e);
            Invalidate();
        }
    }

    /// <summary>A flat button drawn by hand: smooth rounded background (BackColor), a
    /// slight hover and pressed tone, centered text. Disabled keeps the button's normal
    /// text color: WinForms draws disabled text in GrayText, which vanishes on dark cards
    /// and on orange.</summary>
    /// <summary>The tray menu follows the dark window instead of Windows' light popup.</summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        internal DarkMenuRenderer() : base(new DarkMenuColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? DarkText : DarkMuted;
            base.OnRenderItemText(e);
        }
        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => DarkCard;
            public override Color MenuBorder => DarkBorder;
            public override Color MenuItemBorder => DarkBorder;
            public override Color MenuItemSelected => DarkCard2;
            public override Color MenuItemSelectedGradientBegin => DarkCard2;
            public override Color MenuItemSelectedGradientEnd => DarkCard2;
            public override Color MenuItemPressedGradientBegin => DarkCard2;
            public override Color MenuItemPressedGradientEnd => DarkCard2;
            public override Color ImageMarginGradientBegin => DarkCard;
            public override Color ImageMarginGradientMiddle => DarkCard;
            public override Color ImageMarginGradientEnd => DarkCard;
        }
    }

    internal sealed class ReadableButton : Button
    {
        private int _radius = 4;
        private bool _hover, _pressed;

        public ReadableButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Radius { get => _radius; set { _radius = value; Invalidate(); } }

        private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }

        /// <summary>When false, the button's own disabled colors are painted as-is
        /// (the Pair button styles its disabled state itself; dimming it AGAIN here
        /// halved the contrast twice - round-7 L1).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool DimWhenDisabled { get; set; } = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            var fill = BackColor;
            // Disabled reads disabled: the fill leans halfway toward whatever is behind the
            // button and the text halfway toward that.
            if (!Enabled && DimWhenDisabled) fill = Mix(BackColor, Parent?.BackColor ?? BackColor, 0.5f);
            // Hover/pressed lean toward the text color: lighter on dark grey, deeper on orange.
            else if (_pressed) fill = Mix(BackColor, ForeColor, 0.16f);
            else if (_hover) fill = Mix(BackColor, ForeColor, 0.08f);
            var border = FlatAppearance.BorderSize > 0 ? FlatAppearance.BorderColor : Color.Empty;
            PaintRounded(e.Graphics, this, fill, border, _radius);
            var fg = Enabled || !DimWhenDisabled ? ForeColor : Mix(ForeColor, fill, 0.5f);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fg, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues && Enabled)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var ring = RoundedRect(new RectangleF(2.5f, 2.5f, Width - 5, Height - 5), Math.Max(1, _radius - 2));
                using var pen = new Pen(Color.FromArgb(140, ForeColor), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                e.Graphics.DrawPath(pen, ring);
            }
        }
    }

    /// <summary>The one orange button: the window's primary action.</summary>
    /// <summary>Disabled Pair reads as off: neutral grey fill, muted label - never a
    /// muddy half-orange that still looks tappable (broken store / pairing states).</summary>
    private void StylePairEnabled()
    {
        if (_pair.Enabled) StylePrimary(_pair);
        else
        {
            _pair.BackColor = Color.FromArgb(58, 58, 64);
            _pair.ForeColor = DarkMuted;
            _pair.Invalidate();
        }
    }

    internal static void StylePrimary(Button b)
    {
        b.BackColor = Orange;
        b.ForeColor = OnOrange;
        b.FlatAppearance.BorderSize = 0;
        b.Invalidate();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshPhones();
        ActiveControl = _nameBox; // the name box is the obvious first step
    }

    private static Label Header(string text, int x, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        AutoSize = true, Left = x, Top = y,
    };

    private System.Windows.Forms.Timer? _captionTimer;

    /// <summary>Check results ("You're up to date.") show for a few seconds, then the row
    /// goes back to showing the running version - like the phone app.</summary>
    private void ShowCaptionBriefly(string s)
    {
        _updatesResult.Text = s;
        if (_captionTimer == null)
        {
            _captionTimer = new System.Windows.Forms.Timer { Interval = 20000 };
            _captionTimer.Tick += (_, _) =>
            {
                _captionTimer.Stop();
                if (!_downloading && !_manualDownload) _updatesResult.Text = "";
            };
        }
        _captionTimer.Stop();
        _captionTimer.Start();
    }

    /// <summary>The Updates card's manual check: result shows inline, never a tray balloon.</summary>
    private void ManualUpdateCheck()
    {
        if (_manualDownload) { StartUpdateDownload(); return; }
        _checkUpdates.Enabled = false;
        _updatesResult.Text = "Checking...";
        Task.Run(() =>
        {
            (string Version, string ExeUrl, string? Sha256)? newer;
            try { newer = UpdateCheck.LatestNewerThanOrThrow(Program.VersionText); }
            catch
            {
                try { BeginInvoke(new Action(() =>
                {
                    ShowCaptionBriefly("Couldn't check right now. Try again later.");
                    _checkUpdates.Enabled = true;
                })); } catch { }
                return;
            }
            try { BeginInvoke(new Action(() =>
            {
                if (newer != null)
                {
                    Program.AvailableUpdate = newer.Value;
                    _updatesResult.Text = "Version " + newer.Value.Version + " is available.";
                    _manualDownload = true;
                    _checkUpdates.Text = "Download";
                    _checkUpdates.Enabled = true;
                    StylePrimary(_checkUpdates);
                }
                else
                {
                    ShowCaptionBriefly("Up to date");
                    _checkUpdates.Enabled = true;
                }
            })); } catch { }
        });
    }

    /// <summary>Open the verified installer on the UI thread and quit so it can replace the
    /// running exe. A "no" at the Windows permission prompt cancels the launch quietly.</summary>
    private void LaunchUpdate(string path)
    {
        try
        {
            SelfUpdate.LaunchAndQuit(path);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Program.Log("Update launch canceled at the Windows permission prompt.");
            ShowCaptionBriefly("Update canceled.");
            _checkUpdates.Enabled = true;
            _downloading = false;
        }
    }

    /// <summary>Download the update's installer, check it against the release's SHA-256, and
    /// run it. He always taps Update first; after one Windows permission prompt it installs
    /// itself and reopens Zephyr VLC Remote.</summary>
    private void StartUpdateDownload()
    {
        var u = Program.AvailableUpdate;
        if (u == null || _downloading) return;
        _downloading = true;
        _checkUpdates.Enabled = false;
        _updatesResult.Text = "Downloading version " + u.Value.Version + "...";
        var upd = u.Value;
        var progress = new Progress<int>(p => _updatesResult.Text = "Downloading " + p + "%...");
        Task.Run(async () =>
        {
            try
            {
                var path = await SelfUpdate.Download(upd.Version, upd.ExeUrl, upd.Sha256, progress);
                Program.Log("Update " + upd.Version + " downloaded and verified; opening the installer.");
                try { BeginInvoke(new Action(() =>
                {
                    _updatesResult.Text = "Checked. Windows will ask for permission once, then the update installs itself.";
                    LaunchUpdate(path);
                })); } catch { }
            }
            catch (OperationCanceledException)
            {
                Program.Log("Update download stalled (60 seconds of silence) - canceled so it cannot sit on Downloading forever.");
                try { BeginInvoke(new Action(() =>
                {
                    _updatesResult.Text = "The download stopped. Try again.";
                    _checkUpdates.Enabled = true;
                    _downloading = false;
                })); } catch { }
            }
            catch
            {
                Program.Log("Update download failed or failed its check.");
                try { BeginInvoke(new Action(() =>
                {
                    _updatesResult.Text = "The download didn't check out. Try again.";
                    _checkUpdates.Enabled = true;
                    _downloading = false;
                })); } catch { }
            }
        });
    }

    /// <summary>The public-Wi-Fi popup: names the network, one click marks it Private.</summary>
    private void ShowPublicWifiDialog(NetProfile profile)
    {
        if (_wifiDialogOpen) return;
        _wifiDialogOpen = true;
        try
        {
        using var dlg = new WifiDialog(profile);
        switch (dlg.ShowDialog(this))
        {
            case WifiDialog.Result.Home:
                // MarkPrivate waits on the Windows permission prompt (up to 30 s): run it off
                // the UI thread so the window stays alive, like the strip button does.
                Task.Run(() =>
                {
                    var ok = false;
                    try { ok = NetworkCheck.MarkPrivate(profile); } catch { }
                    try { BeginInvoke(new Action(() =>
                    {
                        if (ok)
                        {
                            lock (Program.ConfigSaveLock)
                            {
                                Program.DeclinedNetworks.RemoveAll(n => string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase));
                                if (!Program.HomeNetworks.Any(n => string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase)))
                                    Program.HomeNetworks.Add(profile.Name);
                                Program.TrySaveConfig("the Wi-Fi choice");
                            }
                            _wifiProfile = null;
                            SetWifiStripVisible(false);
                            Program.Log("Network \"" + profile.Name + "\" marked Private from the popup.");
                            NoteDialog.Show(this,
                                "\u2713 Done. Your phone can reach this PC now.\n\nThis Wi-Fi is now marked Private in Windows, and Windows remembers it.");
                        }
                        else
                        {
                            NoteDialog.Show(this,
                                "The change did not go through. Opening the Windows setting so you can switch this Wi-Fi to Private by hand.");
                            OpenWifiSettings();
                        }
                    })); } catch { }
                });
                break;
            case WifiDialog.Result.Settings:
                OpenWifiSettings();
                break;
            case WifiDialog.Result.NotNow:
                lock (Program.ConfigSaveLock)
                {
                    Program.DeclinedNetworks.Add(profile.Name);
                    // Saying "Not now" on a network once marked home means it is not home after all.
                    Program.HomeNetworks.RemoveAll(n => string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase));
                    Program.TrySaveConfig("the Wi-Fi choice");
                }
                Program.Log("Public-network popup declined for \"" + profile.Name + "\" - not asking again.");
                break;
        }
        }
        finally { _wifiDialogOpen = false; }
    }

    private static void OpenWifiSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true });
        }
        catch { /* the popup text still tells him what to do */ }
    }

    /// <summary>Bring the window up from the tray (menu, double-click, or a second launch of the exe).</summary>
    internal void ShowFromTray()
    {
        _allowShow = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // Paired PCs get no popup at login: the helper starts tucked in the tray.
    protected override void SetVisibleCore(bool value)
    {
        if (!_allowShow && Program.Paired) value = false;
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // closing tucks it into the tray, silently; the tray menu's Quit exits for real
            Hide();
            return;
        }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }
}


/// <summary>Shared look for the small dialogs: 20px margins, measured text (never clipped),
/// smooth rounded buttons (4px) right-aligned with 8px gaps.</summary>
internal static class DialogLook
{
    internal const int Margin = 20;
    internal const int Gap = 8;

    /// <summary>A wrapping label exactly as tall as its text at this width.</summary>
    internal static Label Text(string text, Font font, Color fg, int left, int top, int width)
    {
        var h = TextRenderer.MeasureText(text, font, new Size(width, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        return new Label
        {
            Left = left, Top = top, Width = width, Height = h + 2,
            Font = font, ForeColor = fg, Text = text, UseMnemonic = false,
        };
    }

    /// <summary>The dialogs' button: card fill, 1px muted edge, as before; now drawn smooth.</summary>
    internal static HelperWindow.ReadableButton Button(string text, Font font, int minWidth, int height, Color card, Color fg, Color edge)
    {
        var b = new HelperWindow.ReadableButton
        {
            Text = text, Font = font, Height = height,
            Width = Math.Max(minWidth, TextRenderer.MeasureText(text, font).Width + 28),
            BackColor = card, ForeColor = fg,
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = edge;
        return b;
    }

    /// <summary>Lay buttons out left to right, ending at <paramref name="right"/>.</summary>
    internal static void RowRight(int right, int top, params Control[] buttons)
    {
        var x = right;
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            x -= buttons[i].Width;
            buttons[i].Left = x;
            buttons[i].Top = top;
            x -= Gap;
        }
    }
}

/// <summary>The public-Wi-Fi popup: names the network and offers the one-click Private fix,
/// the manual Settings route, or Not now (remembered per Wi-Fi name).</summary>
internal sealed class WifiDialog : Form
{
    public enum Result { None, Home, Settings, NotNow }
    public Result Answer { get; private set; } = Result.None;

    public WifiDialog(NetProfile profile)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        var bg = HelperWindow.DarkBg;
        var card = HelperWindow.DarkCard;
        var text = HelperWindow.DarkText;
        var muted = HelperWindow.DarkMuted;
        HelperWindow.ApplyDarkTitleBar(this);

        Text = "Zephyr VLC Remote";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true; // at login other windows can cover it
        BackColor = bg;
        ForeColor = text;

        const int clientW = 500;
        var m = DialogLook.Margin;
        var contentW = clientW - 2 * m;
        var body = DialogLook.Text(
            "Windows marks this Wi-Fi (" + profile.Name + ") as public, which can block your phone from reaching this PC.\n\n" +
            "If this is your home network, Zephyr VLC Remote can mark it as Private in Windows for you. This also lets Windows use its home-network settings on this Wi-Fi.\n\n" +
            "On a network you don't know (hotel, cafe), choose Not now.",
            new Font("Segoe UI", 9.5f), text, m, m, contentW);
        var small = DialogLook.Text(
            "This only changes this one Wi-Fi. Windows will ask for permission once.",
            new Font("Segoe UI", 8f), text, m, body.Bottom + 12, contentW);
        Button Btn(string label, int width, Result r)
        {
            var b = DialogLook.Button(label, new Font("Segoe UI", 8.5f), width, 32, card, text, muted);
            b.Click += (_, _) => { Answer = r; Close(); };
            return b;
        }
        var home = Btn("This is my home Wi-Fi", 170, Result.Home);
        HelperWindow.StylePrimary(home);
        var settings = Btn("Open Settings instead", 150, Result.Settings);
        var notNow = Btn("Not now", 90, Result.NotNow);
        CancelButton = notNow; // Esc means Not now
        DialogLook.RowRight(clientW - m, small.Bottom + 20, home, settings, notNow);
        ClientSize = new Size(clientW, home.Bottom + m);
        Controls.Add(body);
        Controls.Add(small);
        Controls.Add(home);
        Controls.Add(settings);
        Controls.Add(notNow);
    }

    public new Result ShowDialog(IWin32Window owner)
    {
        base.ShowDialog(owner);
        return Answer;
    }
}


/// <summary>A small themed box whose confirm button says what it does (MessageBox can't relabel).</summary>
internal static class ConfirmDialog
{
    public static bool Ask(IWin32Window? owner, string text, string confirmLabel)
    {
        using var dlg = Build(text, confirmLabel);
        return dlg.ShowDialog(owner) == DialogResult.OK;
    }

    // Construction shared with the CI dialog shots so a capture shows the real thing.
    internal static Form Build(string text, string confirmLabel)
    {
        IWin32Window? owner = null;
        var bg = HelperWindow.DarkBg;
        var card = HelperWindow.DarkCard;
        var fg = HelperWindow.DarkText;
        var muted = HelperWindow.DarkMuted;
        var dlg = new Form
        {
            AutoScaleMode = AutoScaleMode.Dpi,
            Text = "Zephyr VLC Remote",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            BackColor = bg, ForeColor = fg,
        };
        HelperWindow.ApplyDarkTitleBar(dlg);
        const int clientW = 440;
        var m = DialogLook.Margin;
        var body = DialogLook.Text(text, new Font("Segoe UI", 9.5f), fg, m, m, clientW - 2 * m);
        var btnFont = new Font("Segoe UI", 8.5f);
        var yes = DialogLook.Button(confirmLabel, btnFont, 110, 30, card, fg, muted);
        yes.DialogResult = DialogResult.OK;
        var no = DialogLook.Button("Cancel", btnFont, 90, 30, card, fg, muted);
        no.DialogResult = DialogResult.Cancel;
        DialogLook.RowRight(clientW - m, Math.Max(body.Bottom, m + 40) + 20, yes, no);
        dlg.ClientSize = new Size(clientW, yes.Bottom + m);
        dlg.Controls.Add(body);
        dlg.Controls.Add(yes);
        dlg.Controls.Add(no);
        dlg.AcceptButton = no; // Enter on a destructive prompt must mean No
        dlg.CancelButton = no;
        return dlg;
    }
}


/// <summary>A one-button themed note (the Wi-Fi popup's success line).</summary>
internal static class NoteDialog
{
    public static void Show(IWin32Window? owner, string text)
    {
        var bg = HelperWindow.DarkBg;
        var card = HelperWindow.DarkCard;
        var fg = HelperWindow.DarkText;
        var muted = HelperWindow.DarkMuted;
        using var dlg = new Form
        {
            AutoScaleMode = AutoScaleMode.Dpi,
            Text = "Zephyr VLC Remote",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            BackColor = bg, ForeColor = fg,
        };
        HelperWindow.ApplyDarkTitleBar(dlg);
        const int clientW = 440;
        var m = DialogLook.Margin;
        var body = DialogLook.Text(text, new Font("Segoe UI", 9.5f), fg, m, m, clientW - 2 * m);
        var ok = DialogLook.Button("OK", new Font("Segoe UI", 8.5f), 90, 30, card, fg, muted);
        ok.DialogResult = DialogResult.OK;
        DialogLook.RowRight(clientW - m, Math.Max(body.Bottom, m + 40) + 20, ok);
        dlg.ClientSize = new Size(clientW, ok.Bottom + m);
        dlg.Controls.Add(body);
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        dlg.CancelButton = ok;
        dlg.ShowDialog(owner);
    }
}

/// <summary>The Feedback & bugs dialog. Sends through the same two channels as the phone's
/// feedback page: a public GitHub issue or a private form. The preview shows exactly what goes out.</summary>
internal sealed class FeedbackDialog : Form
{
    private const string RepoIssues = "https://github.com/MtndaleRedneck/Zephyr-Dev/issues/new";
    private const string BugFormUrl = "https://docs.google.com/forms/d/e/1FAIpQLSeD1ytVTxoA0GgftLr5wSu2kZm1yxRwCHcOV4Pb100Lm6hLcQ/viewform";

    private readonly TextBox _message;
    private readonly HelperWindow.RoundedPanel _messageFrame;
    private readonly HelperWindow.RoundedPanel _previewCard;
    private DarkScrollStrip _scroll = null!;
    private readonly Label _preview;
    private readonly Button _issue;
    private readonly Button _form;

    public FeedbackDialog()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Zephyr VLC Remote";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);

        // One column, 24px margins (like the main window), measured heights so nothing clips.
        const int clientW = 440, m = 24, contentW = clientW - 2 * m;
        var y = m;
        var title = new Label { Text = "Feedback & bugs", Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), AutoSize = true, Left = m, Top = y, UseMnemonic = false };
        y += title.PreferredHeight + 12;

        // The message box: a smooth rounded frame (4px, 1px edge) with a borderless box inside.
        _messageFrame = new HelperWindow.RoundedPanel { Left = m, Top = y, Width = contentW, Height = 104, Radius = 4 };
        _message = new TextBox
        {
            Multiline = true, Left = 10, Top = 7, Width = contentW - 10 - 4, Height = 104 - 14,
            PlaceholderText = "Your message", BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
        };
        _messageFrame.Controls.Add(_message);
        _messageFrame.Click += (_, _) => _message.Focus();
        y += _messageFrame.Height + 20;

        var whatHeader = new Label { Text = "What will be sent", Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true, Left = m, Top = y, UseMnemonic = false };
        y += whatHeader.PreferredHeight + 8;

        // The preview sits on a card (8px corners, 12px padding) so it reads as one block.
        _previewCard = new HelperWindow.RoundedPanel { Left = m, Top = y, Width = contentW, Height = 168, Radius = 8 };
        _preview = new Label
        {
            Left = 12, Top = 10, Width = contentW - 24, Height = 168 - 20,
            Font = new Font("Segoe UI", 8.5f), AutoEllipsis = false, UseMnemonic = false,
        };
        _previewCard.Controls.Add(_preview);
        y += _previewCard.Height + 20;

        // Measured, never fixed: at larger Windows text a fixed 130px puts the words on the borders.
        var issueW = Math.Max(130, TextRenderer.MeasureText("Open GitHub issue", Font).Width + 32);
        var formW = Math.Max(130, TextRenderer.MeasureText("Open private form", Font).Width + 32);
        _issue = new HelperWindow.ReadableButton { Text = "Open GitHub issue", Width = issueW, Height = 32, FlatStyle = FlatStyle.Flat, Enabled = false };
        _form = new HelperWindow.ReadableButton { Text = "Open private form", Width = formW, Height = 32, FlatStyle = FlatStyle.Flat, Enabled = false };
        // Both actions together at the right, 8px apart; the orange one last.
        DialogLook.RowRight(clientW - m, y, _issue, _form);
        y += 32 + m;
        ClientSize = new Size(clientW, y);

        // The native scrollbar stays (all scrolling behavior intact) but is covered by a
        // dark strip that follows the theme: WinForms scrollbars can't be themed.
        _scroll = new DarkScrollStrip(_message);
        _messageFrame.Controls.Add(_scroll);
        _scroll.BringToFront();
        var scrollTimer = new System.Windows.Forms.Timer { Interval = 60 };
        scrollTimer.Tick += (_, _) => _scroll.SyncThumb();
        scrollTimer.Start();
        Disposed += (_, _) => { scrollTimer.Stop(); scrollTimer.Dispose(); };

        _message.TextChanged += (_, _) => RefreshPreview();
        _issue.Click += (_, _) => OpenUrl(IssueUrl());
        _form.Click += (_, _) => OpenUrl(FormUrl());

        Controls.Add(title);
        Controls.Add(_messageFrame);
        Controls.Add(whatHeader);
        Controls.Add(_previewCard);
        Controls.Add(_issue);
        Controls.Add(_form);
        ApplyTheme();
        RefreshPreview();
    }

    private void ApplyTheme()
    {
        var bg = HelperWindow.DarkBg;
        var card = HelperWindow.DarkCard;
        var cardBtn = HelperWindow.DarkCard2;
        var text = HelperWindow.DarkText;
        var muted = HelperWindow.DarkMuted;
        BackColor = bg;
        ForeColor = text;
        var border = HelperWindow.DarkBorder;
        foreach (Control c in Controls) { c.ForeColor = text; }
        _messageFrame.BackColor = card;
        _messageFrame.BorderColor = border;
        _previewCard.BackColor = card;
        _previewCard.BorderColor = border;
        _message.BackColor = card;
        _message.ForeColor = text;
        _scroll.Track = Color.FromArgb(32, 34, 44);
        _scroll.Thumb = Color.FromArgb(74, 80, 96);
        _scroll.Arrows = muted;
        _scroll.Card = card;
        _scroll.Invalidate();
        _preview.ForeColor = text;
        _preview.BackColor = card;
        _issue.BackColor = cardBtn;
        _issue.ForeColor = text;
        _issue.FlatAppearance.BorderSize = 0;
        HelperWindow.StylePrimary(_form);
        TitleBarTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TitleBarTheme();
    }

    private void TitleBarTheme()
    {
        if (!IsHandleCreated) return;
        var dark = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void RefreshPreview()
    {
        var msg = Clean(_message.Text, 3000);
        // Long messages preview their start and keep the footer: the preview always shows
        // everything else that goes out.
        var shortMsg = msg.Length > 280 ? msg[..280] + "…" : msg;
        _preview.Text = shortMsg + "\n\nZephyr VLC Remote PC version: " + Program.VersionText +
            "\nWindows version: " + OsVersion() +
            "\nDevice: PC" +
            "\n\nNothing else. No PC name, user name, Wi-Fi, phones, or paths.";
        var has = !string.IsNullOrWhiteSpace(msg);
        _issue.Enabled = has;
        _form.Enabled = has;
    }

    private static string OsVersion()
    {
        var v = Environment.OSVersion.Version;
        return v.Build >= 22000 ? $"Windows 11 (build {v.Build})" : $"Windows 10 (build {v.Build})";
    }

    private static string Clean(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s.Substring(0, max);
    }

    /// <summary>A slim dark strip drawn over the message box's native scrollbar. The native
    /// bar keeps working underneath (wheel, keys, caret); this paints the theme look on top
    /// and forwards drags and page clicks to the textbox.</summary>
    private sealed class DarkScrollStrip : Control
    {
        internal Color Track, Thumb, Arrows, Card;
        private readonly TextBox _box;
        private int _first, _lines, _visible;
        private bool _dragging;
        private int _dragY, _dragFirst;

        internal DarkScrollStrip(TextBox box)
        {
            _box = box;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            var w = SystemInformation.VerticalScrollBarWidth;
            var edge = box.BorderStyle == BorderStyle.None ? 0 : 1; // a borderless box's bar sits flush
            SetBounds(box.Left + box.Width - w - edge, box.Top + edge, w, box.Height - 2 * edge);
            TabStop = false;
            SyncThumb();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

        internal void SyncThumb()
        {
            if (_box.IsDisposed || !_box.IsHandleCreated) return;
            var first = (int)SendMessage(_box.Handle, 0x00CE /* EM_GETFIRSTVISIBLELINE */, IntPtr.Zero, IntPtr.Zero);
            var lines = (int)SendMessage(_box.Handle, 0x00BA /* EM_GETLINECOUNT */, IntPtr.Zero, IntPtr.Zero);
            var lh = Math.Max(1, TextRenderer.MeasureText("Ag", _box.Font).Height);
            var visible = Math.Max(1, (_box.ClientSize.Height - 8) / lh);
            if (first != _first || lines != _lines || visible != _visible)
            {
                _first = first; _lines = lines; _visible = visible;
                Invalidate();
            }
        }

        private bool Needed => _lines > _visible;
        private int TrackH => Height - 24;
        private int ThumbH => Math.Max(18, TrackH * _visible / Math.Max(1, _lines));
        private int MaxFirst => Math.Max(1, _lines - _visible);
        private int ThumbY => 12 + (TrackH - ThumbH) * Math.Min(_first, MaxFirst) / MaxFirst;

        protected override void OnPaint(PaintEventArgs e)
        {
            // The strip blends into the box; the track and thumb are smooth slim pills.
            e.Graphics.Clear(Card);
            if (!Needed) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            // arrows
            using var ab = new SolidBrush(Arrows);
            var cx = Width / 2f;
            g.FillPolygon(ab, new[] { new PointF(cx, 3), new PointF(cx - 3.5f, 8), new PointF(cx + 3.5f, 8) });
            g.FillPolygon(ab, new[] { new PointF(cx, Height - 3), new PointF(cx - 3.5f, Height - 8), new PointF(cx + 3.5f, Height - 8) });
            const float pill = 6f;
            var px = (Width - pill) / 2f;
            using (var track = HelperWindow.RoundedRect(new RectangleF(px, 12, pill, TrackH), pill / 2))
            using (var trb = new SolidBrush(Track))
                g.FillPath(trb, track);
            using var thumb = HelperWindow.RoundedRect(new RectangleF(px, ThumbY, pill, ThumbH), pill / 2);
            using var tb = new SolidBrush(Thumb);
            g.FillPath(tb, thumb);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Needed || e.Button != MouseButtons.Left) return;
            if (e.Y >= ThumbY && e.Y <= ThumbY + ThumbH)
            {
                _dragging = true;
                _dragY = e.Y;
                _dragFirst = _first;
                Capture = true;
            }
            else
            {
                SendMessage(_box.Handle, 0x00B5 /* EM_SCROLL */,
                    (IntPtr)(e.Y < ThumbY ? 2 /* SB_PAGEUP */ : 3 /* SB_PAGEDOWN */), IntPtr.Zero);
                SyncThumb();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            var target = _dragFirst + (e.Y - _dragY) * MaxFirst / Math.Max(1, TrackH - ThumbH);
            var delta = Math.Max(0, Math.Min(MaxFirst, target)) - _first;
            if (delta != 0)
            {
                SendMessage(_box.Handle, 0x00B6 /* EM_LINESCROLL */, IntPtr.Zero, (IntPtr)delta);
                SyncThumb();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            SendMessage(_box.Handle, 0x00B6 /* EM_LINESCROLL */, IntPtr.Zero, (IntPtr)(-3 * Math.Sign(e.Delta)));
            SyncThumb();
        }
    }

    private string IssueUrl()
    {
        var msg = Clean(_message.Text, 4000);
        var title = "Feedback: " + (msg.Length <= 60 ? msg : msg.Substring(0, 57) + "...");
        // Trim the message, never the body: the version lines always survive a long report.
        string Body(string m) => m + "\n\nZephyr VLC Remote PC version: " + Program.VersionText + "\nWindows version: " + OsVersion();
        var url = RepoIssues + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(Body(msg));
        while (url.Length > 8000 && msg.Length > 280)
        {
            msg = Clean(msg, msg.Length / 2);
            url = RepoIssues + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(Body(msg));
        }
        return url;
    }

    private string FormUrl()
    {
        var msg = Clean(_message.Text, 3000);
        return BugFormUrl +
            "?entry.1980878269=" + Uri.EscapeDataString(msg) +
            "&entry.857472167=" + Uri.EscapeDataString("PC") +
            "&entry.2017214675=" + Uri.EscapeDataString(OsVersion()) +
            "&entry.446486963=" + Uri.EscapeDataString(Program.VersionText);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}

