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
    internal const int HttpPort = 8493;      // HTTPS port. Earlier plain-http builds used 8492 (their http:// reservation is left alone, never deleted); BingeMote keeps 8488; Zephyr Player owns 47824
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
    /// suggested anywhere without explicit confirmation.</summary>
    internal static string StoreBrokenReason = "could not be read";
    internal static int PhonesCount { get { lock (PhonesLock) { return Phones.Count; } } }
    /// <summary>The name typed for the phone that is about to pair; consumed by a successful pairing.</summary>
    internal static string? PendingPhoneName;
    /// <summary>The window re-reads the phone list on the UI thread when this fires (any thread).</summary>
    internal static Action? PhonesChanged;
    /// <summary>Unused retained round-trip data: Wi-Fi names from an earlier build's removed prompt. Kept so existing config files load and save unchanged; nothing in the UI reads it.</summary>
    internal static readonly List<string> DeclinedNetworks = new();
    /// <summary>Unused retained round-trip data: Wi-Fi names marked home by an earlier build's removed prompt. Kept so existing config files load and save unchanged; nothing in the UI reads it.</summary>
    internal static readonly List<string> HomeNetworks = new();
    /// <summary>More phones than this on one PC is a mistake, not a household.</summary>
    internal const int MaxPhones = 8;
    internal static bool Paired => PhonesCount > 0;
    /// <summary>The folder the phone's Movies tab opens; set from this window or from the phone.</summary>
    internal static string? MoviesFolder;

    /// <summary>Keep the PC awake while a video is loaded in VLC and VLC is not minimized
    /// (screen may still turn off). He asked for the behavior plus a setting to disable it
    /// (Nov 3); the ON default is the delegated design choice. A saved choice is kept.</summary>
    internal static bool KeepAwakeEnabled = true;

    /// <summary>"Show volume on PC screen" - the small pop-up on each phone volume change. Default ON.</summary>
    internal static bool VolumeOsdEnabled = true;
    internal static string? PairCode;
    /// <summary>When the on-screen code was made. A Pair session lasts PairSessionLifetime, then closes by itself.</summary>
    internal static DateTime PairCodeIssuedUtc = DateTime.MinValue;
    internal static readonly TimeSpan PairSessionLifetime = TimeSpan.FromMinutes(10);
    /// <summary>The version that last ran; a change means an upgrade just landed.</summary>
    internal static string? LastRunVersion;
    /// <summary>A newer published helper version, found by the background update check.</summary>
    internal static (string Version, string ExeUrl, string? Sha256)? AvailableUpdate;
    internal static int PairFailures;
    internal const int MaxPairFailures = 5;

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
            // Only a folder inside that user's own profile is accepted (validated again inside
            // RunElevated); anything else leaves this process on its own data folder.
            string? vsid = null;
            try { if (!string.IsNullOrWhiteSpace(sid)) vsid = new System.Security.Principal.SecurityIdentifier(sid).Value; } catch { }
            // Program.DataDir is NOT reassigned here: RunElevated validates the supplied folder once, right
            // before use, and its fallback must stay this process's own trusted default.
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
        if (args.Contains("--dialog-shots"))
        {
            ApplicationConfiguration.Initialize();
            DialogCapture.Run(Environment.GetEnvironmentVariable("BINGEMOTE_DIALOG_SHOTS") ?? "dialog-shots");
            return 0;
        }

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
            if (shotMode == "pairing")
            {
                // A live code showing, exactly as after tapping Pair.
                Program.PendingPhoneName = "Shot Phone";
                Program.PairCode = Program.NewPairCode();
                shot.RunStatusTickForShot();
            }
            else if (shotMode is "broken-store" or "remove" or "remove-all" or "tray" or "remove-paths")
            {
                // State-asserting captures: the real handlers run, the state is checked, and a
                // <shot>.state.txt sidecar records what was asserted. Any failed assert throws.
                Program.RunStateShot(shot, shotMode, shotPath);
                return 0;
            }
#endif
            shot.ShowOffscreenAndCapture(shotPath);
            return 0;
        }

        // CI-only: render the volume pop-up (SPEC's 60% example, normal + muted) and exit.
        if (args.Contains("--osd-shot"))
        {
            var osdPath = Environment.GetEnvironmentVariable("BINGEMOTE_OSD_SHOT")
                ?? Path.Combine(DataDir, "osd.png");
            var osdMutedPath = Environment.GetEnvironmentVariable("BINGEMOTE_OSD_SHOT_MUTED")
                ?? Path.Combine(DataDir, "osd-muted.png");
            VolumeOsd.CaptureShot(osdPath, osdMutedPath, 60);
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
            // Do NOT kill a WORKING helper before this copy can serve (round-11 R11-1):
            // the UDP discovery rule names the running copy's path, so a copy launched
            // from another folder lands on the firewall setup prompt after the takeover -
            // the old copy was already gone and nothing listened until the prompt was
            // answered. Set up FIRST, while the old copy keeps serving; a declined or
            // failed setup leaves it untouched. Same-path updates never get here: their
            // rules already match and the check below passes at once.
            FirewallSetup.LastReadFailed = false;
            if (!(FirewallSetup.SetupIsCurrent()))
            {
                Log("Update launched from another folder needs its own firewall setup first - the running copy keeps serving meanwhile.");
                if (FirewallSetup.LastReadFailed)
                {
                    // The firewall/ACL API could not be read at all: elevation cannot fix
                    // that, so no setup prompt - one plain note and close (round-9 N9-8).
                    NoteDialog.Show(null, "Windows Firewall settings could not be read on this PC. Zephyr VLC Remote cannot check its network permission and will close now; a restart of Windows usually fixes the read.");
                    return 0;
                }
                if (!FirewallSetup.ExplainAndRelaunch()) return 0;
                if (!(FirewallSetup.SetupIsCurrent()))
                {
                    // Do NOT say the running copy "was left alone": the elevated setup
                    // deletes and re-adds the rules, so a mid-failure may have rewritten
                    // them for this copy's path (round-12). It was not closed, and its
                    // own next start re-checks and offers to set up.
                    NoteDialog.Show(null,
                        "Setup did not finish, so the phone cannot reach this copy yet. The copy that was already running was not closed. If the phone loses it, open it again - it will check its setup and offer to fix it.");
                    return 0;
                }
            }
            Log("Update launched while an older copy ran - asked it to quit, taking over.");
            oldQuit.Set();
            // The old copy exits and releases the mutex; take it over. 30 seconds, not
            // 10 (round-12): the quit signal was already sent, so giving up early leaves
            // a gap with no helper listening until he reopens one.
            var tookOver = false;
            for (var i = 0; i < 120 && !tookOver; i++)
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
        VlcRotation.Start(); // the app-owned rotation pass: 15 s timer + startup kick (no window needed)
        if (LastRunVersion != VersionText)
        {
            // An upgrade just landed. Windows caches icons aggressively; tell the shell
            // associations changed so the desktop shortcut and taskbar pin pick up the
            // new icon now instead of after a sign-out.
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x3000 /* SHCNF_IDLIST|SHCNF_FLUSH */, IntPtr.Zero, IntPtr.Zero); } catch { }
            LastRunVersion = VersionText;
            try { SaveConfig(); } catch (Exception ex) { Log("Could not save the version stamp: " + ex.Message); }
        }
        Log("Started. " + (!Paired ? "Not paired - open the window and tap Pair to get a code." : "Paired (" + PhonesCount + (PhonesCount == 1 ? " phone" : " phones") + ") - starting tucked in the tray."));
        RegisterAutostart();
        try { RegisterApplicationRestart("/restarted", 0); } catch { } // crash or hang: Windows relaunches us

    {
        // The live check runs on EVERY start (COM read, no admin): a versioned marker
        // must never vouch for rules deleted or changed after it was written, and a
        // marker file can even be copied between machines (round-8). The marker is
        // still stamped (the upgrade smoke asserts it) but nothing trusts it.
        FirewallSetup.LastReadFailed = false;
        if (FirewallSetup.SetupIsCurrent())
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
            if (FirewallSetup.SetupIsCurrent())
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
        if (overrideCode is { Length: 4 } && overrideCode.All(char.IsDigit))
        {
            PairCode = overrideCode;
            PairCodeIssuedUtc = DateTime.UtcNow;
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
        // The watchdog task is registered only by the elevated, journaled setup step (never from this unelevated start).

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

    internal static string NewPairCode() => RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4");

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
        // A fresh install starts unpaired: nothing is carried over from another product,
        // so every phone is authorized by an explicit Pair on this PC.
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
            KeepAwakeEnabled = !doc.RootElement.TryGetProperty("keepAwake", out var ka) || ka.ValueKind != JsonValueKind.False;
            VolumeOsdEnabled = !doc.RootElement.TryGetProperty("volumeOsd", out var vo) || vo.ValueKind != JsonValueKind.False;
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
            var vtRaw = doc.RootElement.TryGetProperty("vlcTarget", out var vtp) ? vtp.GetString() : null;
            var vtUnwrapped = vtRaw == null ? null : Dpapi.Unwrap(vtRaw);
            if (vtRaw != null && vtUnwrapped == null)
            {
                // Same fail-closed rule: the rotation target is the only record of a
                // revocation in progress.
                StoreBroken = true;
                StoreBrokenReason = "could not be decrypted";
                Log("The stored VLC password-change target could not be decrypted. The file is left untouched; pairing and saving are off. Reopen Zephyr VLC Remote to try again.");
                lock (PhonesLock) { Phones.Clear(); } DeclinedNetworks.Clear(); HomeNetworks.Clear(); MoviesFolder = null;
                return;
            }
            var vtLanded = doc.RootElement.TryGetProperty("vlcLanded", out var vll) && vll.ValueKind == JsonValueKind.True;
            var vtGen = doc.RootElement.TryGetProperty("vlcTargetGeneration", out var vgp) && vgp.TryGetInt32(out var g0) ? g0 : 0;
            // Field migration: a legacy pending vlcrc write becomes the rotation target once
            // (landed=false); the legacy key is gone from the next save.
            if (vtUnwrapped == null && pvUnwrapped != null) { vtUnwrapped = pvUnwrapped; vtLanded = false; vtGen = 1; }
            _rotationFromConfig = (vtUnwrapped, vtLanded, vtGen);
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
        // A rotation in progress survives a helper restart: the app-owned pass converges it.
        var rot = _rotationFromConfig;
        _rotationFromConfig = default;
        if (rot.target != null || rot.gen != 0) VlcRotation.LoadPersisted(rot.target, rot.landed, rot.gen);
    }

    private static (string? target, bool landed, int gen) _rotationFromConfig;

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
                    vlcTarget = VlcRotation.TargetValue is { } vt ? Dpapi.Wrap(vt) : null,
                    vlcLanded = VlcRotation.Landed,
                    vlcTargetGeneration = VlcRotation.Generation,
                    moviesFolder = MoviesFolder,
                    keepAwake = KeepAwakeEnabled,
                    volumeOsd = VolumeOsdEnabled,
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

    /// <summary>Mutate + persist + roll back as one unit under the OUTER save lock. The
    /// in-memory state changes only if it also reached disk: a refused save runs
    /// <paramref name="rollback"/> before the lock is released, so no concurrent save can
    /// persist, and no reader can keep, a state that never made it to disk. Returns false
    /// (and logs) when the save failed and the change was rolled back.</summary>
    internal static bool MutateAndPersist(Action mutate, Action rollback, string what)
    {
        lock (ConfigSaveLock)
        {
            mutate();
            try { SaveConfig(); return true; }
            catch (Exception ex)
            {
                try { rollback(); } catch (Exception rex) { Log("Rollback after a failed save of " + what + " also failed: " + rex.Message); }
                Log("Could not save " + what + " (change undone): " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Wi-Fi choice (home / not now) with rollback of both lists on a failed save.</summary>
    internal static bool SetNetworkChoice(string name, bool home)
    {
        string[] priorDeclined = Array.Empty<string>(), priorHome = Array.Empty<string>();
        return MutateAndPersist(
            () =>
            {
                priorDeclined = DeclinedNetworks.ToArray(); priorHome = HomeNetworks.ToArray();
                if (home)
                {
                    DeclinedNetworks.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    if (!HomeNetworks.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) HomeNetworks.Add(name);
                }
                else
                {
                    DeclinedNetworks.Add(name);
                    HomeNetworks.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                }
            },
            () => { DeclinedNetworks.Clear(); DeclinedNetworks.AddRange(priorDeclined); HomeNetworks.Clear(); HomeNetworks.AddRange(priorHome); },
            "the Wi-Fi choice");
    }

    /// <summary>Phone removal that is on disk before it is final; a refused save puts the phones back.</summary>
    internal static bool RemovePhonesAndPersist(Func<PairedPhone[], PairedPhone[]> pick)
    {
        PairedPhone[] removed = Array.Empty<PairedPhone>();
        PairedPhone[] before = Array.Empty<PairedPhone>();
        return MutateAndPersist(
            () => { lock (PhonesLock) { before = Phones.ToArray(); removed = pick(before); foreach (var r in removed) Phones.Remove(r); } },
            () => { lock (PhonesLock) { Phones.Clear(); Phones.AddRange(before); } },
            "the phone removal");
    }

    /// <summary>Removal + VLC password rotation as ONE transaction: the phones and the new
    /// rotation target change together, or a refused save rolls both back (reverse order) and
    /// the UI says "still paired, nothing changed". The password is generated by the caller
    /// BEFORE the lock. Grants are untouched here; the pass re-arms them on the landing edge.
    /// Returns the outcome and the target's generation (the UI picks its copy from the
    /// persisted state for exactly this generation).</summary>
    internal static (bool ok, int gen) RemovePhonesAndRotatePersist(Func<PairedPhone[], PairedPhone[]> pick, string newVlcPassword)
    {
        PairedPhone[] before = Array.Empty<PairedPhone>();
        (string? t, bool landed, int gen) backup = default;
        var stagedGen = 0; // captured INSIDE the transaction: a read after the locks could return a NEWER rotation's generation and misattribute this removal's outcome
        bool ok;
        // Staging is serialized under LaunchWriteMutex (the lock ORDER: LaunchWriteMutex >
        // ConfigSaveLock > PhonesLock/Gate): a pass that snapshotted the previous target and is
        // waiting for the mutex can then never write it over this newer stage - the apply's
        // in-mutex recheck catches any stage that landed first, and any later stage waits for
        // the apply. The UI thread can block here for the duration of a Play inline launch.
        lock (VlcRotation.LaunchWriteMutex)
        {
            ok = MutateAndPersist(
                () =>
                {
                    lock (PhonesLock) { before = Phones.ToArray(); foreach (var r in pick(before)) Phones.Remove(r); }
                    backup = VlcRotation.BackupTarget();
                    VlcRotation.SetTargetUnderConfigSaveLock(newVlcPassword);
                    stagedGen = VlcRotation.Generation; // exactly this stage's committed generation
                },
                () =>
                {
                    VlcRotation.RestoreTargetUnderConfigSaveLock(backup);
                    lock (PhonesLock) { Phones.Clear(); Phones.AddRange(before); }
                },
                "the phone removal");
        }
        return (ok, stagedGen);
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

    /// <summary>"1.0.1" - the running build, for the Updates card and feedback reports.</summary>
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

        private static string ModalText(Control root)
        {
            var sb = new StringBuilder();
            void Walk(Control c)
            {
                foreach (Control k in c.Controls)
                {
                    if (k is Label || k is Button) sb.Append(k.Text).Append('|');
                    Walk(k);
                }
            }
            Walk(root);
            return sb.ToString();
        }

        // Runs trigger() (which opens a REAL modal dialog). A timer polls (bounded: 20 x 500 ms) for the
        // modal, reads its text, optionally captures its pixels, and clicks the button carrying `click`.
        // Any failure (no modal in time, no matching button, an exception while capturing) sets `failure`,
        // closes the modal so ShowDialog returns instead of hanging the run, and then THROWS after trigger()
        // returns. A silent miss can never read as a pass and a failure never leaves a dialog open.
        private static string DriveModal(Action trigger, string? png, DialogResult click)
        {
            var seen = "";
            string? failure = null;
            var done = false;
            var ticks = 0;
            var stuck = 0;
            Form? driven = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (_, _) =>
            {
                ticks++;
                Form? modal = null;
                foreach (Form f in Application.OpenForms) { if (f.Modal && f.Visible) { modal = f; break; } }
                if (driven != null)
                {
                    // Already clicked. The modal must now be gone; if it is still up after ~2 s, fail and force it
                    // closed so ShowDialog returns instead of hanging the process.
                    if (modal != null && ++stuck >= 4)
                    {
                        failure = "the dialog stayed open after the click";
                        timer.Stop();
                        try { modal.DialogResult = DialogResult.Abort; modal.Close(); } catch { }
                    }
                    else if (modal == null) timer.Stop();
                    return;
                }
                if (modal == null)
                {
                    if (ticks >= 20) { failure = "no modal dialog appeared within 10 s"; timer.Stop(); }
                    return;
                }
                timer.Stop();   // no re-entry while driving (DoEvents below)
                try
                {
                    seen = ModalText(modal);
                    if (png != null)
                    {
                        Application.DoEvents();
                        using var bmp = new Bitmap(modal.Width, modal.Height);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            var hdc = g.GetHdc();
                            bool ok = PrintWindowForShot(modal.Handle, hdc, 2);
                            g.ReleaseHdc(hdc);
                            if (!ok) { g.Clear(modal.BackColor); modal.DrawToBitmap(bmp, new Rectangle(0, 0, modal.Width, modal.Height)); }
                        }
                        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    if (ClickDialogButton(modal, click)) { done = true; driven = modal; timer.Start(); }
                    else failure = "no modal button matched " + click;
                }
                catch (Exception ex) { failure = "modal drive failed: " + ex.GetType().Name; }
                if (failure != null) { timer.Stop(); try { modal.DialogResult = DialogResult.Abort; modal.Close(); } catch { } }
            };
            timer.Start();
            trigger();
            timer.Stop();
            if (failure != null) throw new InvalidOperationException("state-shot: " + failure);
            if (!done) throw new InvalidOperationException("state-shot: the dialog was never driven");
            return seen;
        }

        internal static void RunStateShot(HelperWindow shot, string mode, string shotPath)
        {
            var dir = Path.Combine(Path.GetTempPath(), "zvr-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            RedirectDataDirForTests(dir);
            StoreBroken = false;
            lock (PhonesLock) { Phones.Clear(); }
            var state = new StringBuilder();
            void Fail(string why) => throw new InvalidOperationException("state-shot " + mode + ": " + why);
            int Reloaded() { lock (PhonesLock) { Phones.Clear(); } StoreBroken = false; LoadConfig(); return PhonesCount; }
            PairedPhone First() { lock (PhonesLock) { return Phones[0]; } }
            shot.StartPosition = FormStartPosition.Manual;
            shot.Location = new Point(0, 0);
            shot.ShowInTaskbar = false;
            shot.Show();
            Application.DoEvents();

            void RemoveSequence(bool all, string? png)
            {
                var tag = all ? "remove_all" : "remove";
                lock (PhonesLock) { Phones.Clear(); }
                AddPhoneAndPersist("shot-token-a", "Shot Phone A");
                AddPhoneAndPersist("shot-token-b", "Shot Phone B");
                shot.HookRefreshPhones();
                Action trigger = () => { if (all) shot.HookRemoveAll(); else shot.HookRemovePhone(First()); };
                var text = DriveModal(trigger, png, DialogResult.Cancel);
                if (!text.Contains(all ? "Remove all phones?" : "Remove Shot Phone A?")) Fail(tag + " dialog text was not the production text");
                state.AppendLine(tag + "_dialog_text_ok=1");
                if (PhonesCount != 2) Fail(tag + " cancel changed the phones in memory");
                state.AppendLine(tag + "_cancel_kept=" + PhonesCount);
                if (Reloaded() != 2) Fail(tag + " cancel changed the saved file");
                state.AppendLine(tag + "_cancel_reload=" + PhonesCount);
                shot.HookRefreshPhones();
                DriveModal(trigger, null, DialogResult.OK);
                var want = all ? 0 : 1;
                // The handler completes the removal OFF the UI thread by design (Task.Run, so the
                // confirm never freezes the window while the transaction waits on LaunchWriteMutex):
                // a returned DriveModal no longer means the removal finished. The in-memory count is
                // no completion signal either - inside the transaction memory flips BEFORE the save,
                // and polling Reloaded() mid-transaction would repopulate the list the in-flight save
                // is about to serialize (corrupting the removal it observes). The durable postcondition
                // is the operation's completion: _removing released in the handler's finally, after the
                // continuation ran. Then memory AND the saved file hold the outcome and the asserts
                // below read a stable state. Bounded: an operation that never settles fails, never hangs.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (shot.HookRemoving && DateTime.UtcNow < deadline) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                if (shot.HookRemoving) Fail(tag + " did not settle after the confirm");
                if (PhonesCount != want) Fail(tag + " confirm left " + PhonesCount);
                state.AppendLine(tag + "_confirm_left=" + PhonesCount);
                if (Reloaded() != want) Fail(tag + " confirm was not saved to the file");
                state.AppendLine(tag + "_reload_left=" + PhonesCount);
                if (!all && First().Name != "Shot Phone B") Fail("the wrong phone was removed");
            }

            if (mode == "tray")
            {
                var items = shot.HookTrayMenuItems();
                if (items.Length != 2 || items[0] != "Show Zephyr VLC Remote" || items[1] != "Quit Zephyr VLC Remote") Fail("unexpected tray menu items");
                if (!shot.HookTrayVisible) Fail("tray icon is not visible");
                shot.HookCaptureTrayMenu(shotPath);
                state.AppendLine("tray_items=" + string.Join(";", items));
                state.AppendLine("tray_icon_visible=1");
            }
            else if (mode == "broken-store")
            {
                File.WriteAllText(ConfigPath, "{ this is not valid json");
                var before = File.ReadAllBytes(ConfigPath);
                StoreBroken = false;
                LoadConfig();
                if (!StoreBroken) Fail("a damaged config was not detected");
                state.AppendLine("store_broken=1");
                state.AppendLine("store_broken_reason=" + StoreBrokenReason);
                // Two truthful captures: the window while the store is broken (the helper window has no
                // on-window indicator for this state, so this PNG can equal the plain window), and the
                // fail-closed note that the real Pair path shows. The state asserts are on the note and the store.
                shot.HookCaptureWindow(shotPath);
                var notePath = Path.Combine(Path.GetDirectoryName(shotPath) ?? ".", "dialog-broken-store-note.png");
                state.AppendLine("window_png=" + Path.GetFileName(shotPath) + " (no on-window broken-store indicator exists in the product)");
                state.AppendLine("note_png=" + Path.GetFileName(notePath));
                var text = DriveModal(() => shot.HookStartPairing("Shot Name"), notePath, DialogResult.OK);
                if (!text.Contains("saved pairing data on this PC")) Fail("pairing note was not the fail-closed text");
                state.AppendLine("dialog_text_ok=1");
                state.AppendLine("pair_code_minted=" + (PairCode != null || PendingPhoneName != null ? 1 : 0));
                if (PairCode != null || PendingPhoneName != null) Fail("a pairing code was minted on a broken store");
                if (!File.ReadAllBytes(ConfigPath).SequenceEqual(before)) Fail("the damaged file was changed");
                state.AppendLine("file_unchanged=1");
                var refused = false;
                try { AddPhoneAndPersist("shot-token-x", "Shot Phone X"); } catch (InvalidOperationException) { refused = true; }
                if (!refused) Fail("a save was not refused on a broken store");
                if (!File.ReadAllBytes(ConfigPath).SequenceEqual(before)) Fail("the damaged file changed after a refused save");
                state.AppendLine("add_refused=1");
            }
            else if (mode == "remove-paths")
            {
                RemoveSequence(false, null);
                RemoveSequence(true, null);
            }
            else
            {
                RemoveSequence(mode == "remove-all", shotPath);
            }
            File.WriteAllText(shotPath + ".state.txt", state.ToString());
            shot.Close();
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
}
