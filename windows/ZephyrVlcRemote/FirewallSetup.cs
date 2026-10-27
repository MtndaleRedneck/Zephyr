using System.Diagnostics;

namespace ZephyrVlcRemote;

/// <summary>
/// The one-time firewall step. Windows needs admin rights to add a firewall rule and the
/// listen permission, so the helper restarts itself once with the UAC prompt, adds a
/// Private-networks-only rule, checks both are really there, and leaves a versioned
/// marker so it never asks again. The marker is VERSIONED: when the required rule or
/// ACL shape changes (the round-7 http.sys shape vs the old exe-scoped shape), an
/// existing install verifies the ACTUAL shape once and re-runs this setup if anything
/// is old, instead of trusting a marker an older build wrote.
/// </summary>
internal static class FirewallSetup
{
    /// <summary>First line of the firewall.ok marker. Bump when the required shape changes.</summary>
    internal const string MarkerVersion = "v2";

    /// <summary>Who the port's URL reservation belongs to.</summary>
    internal enum AclOwnership { None, Ours, Legacy, Foreign }

    /// <summary>Runs inside the elevated copy. Returns the process exit code.
    /// [userSid] is the UNelevated user's SID: the listen permission is scoped to that
    /// account, not everyone. [userDataDir] is that user's data folder, so the markers
    /// land where the user's copy reads them even when another admin approved the UAC.
    /// Both fall back to this process's own identity/folder for direct runs.
    /// Exit 4 means the port is reserved by another app or another Windows user; their
    /// reservation is NEVER overwritten - the caller explains the conflict instead.</summary>
    public static int RunElevated(string? userSid, string? userDataDir)
    {
        try
        {
            // Validate the passed SID before it goes anywhere near an SDDL string; a
            // missing or malformed value falls back to this process's own identity.
            string? sid = null;
            if (!string.IsNullOrWhiteSpace(userSid))
            {
                try { sid = new System.Security.Principal.SecurityIdentifier(userSid).Value; }
                catch { sid = null; }
            }
            sid ??= System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null)
            {
                Console.Error.WriteLine("The listen permission needs a Windows account to scope to; none was available.");
                return 3;
            }
            var dataDir = userDataDir ?? Program.DataDir;
            // Classify the port reservation FIRST (round-9 N9-5): when it belongs to
            // another app or another Windows user, setup stops BEFORE touching any
            // machine-wide rule - rewriting the shared rules first would break the
            // other install even though setup then stopped with the conflict exit.
            var aclOwner = ClassifyUrlAcl(sid);
            if (aclOwner == AclOwnership.Foreign)
            {
                Program.Log("firewall setup: port " + Program.HttpPort + " is reserved by another app or Windows user; left untouched.");
                Console.Error.WriteLine("The network port Zephyr VLC Remote needs is reserved for another app or another Windows user on this PC.");
                return 4;
            }
            Run("netsh", $"advfirewall firewall delete rule name=\"{Program.LegacyFirewallRule}\""); // 1.x installs
            // Re-add our own rules from scratch: by name they are ours, whatever shape an
            // older build gave them (a 1.x exe-scoped TCP rule included). BingeMote's
            // rules (8488/8489) and any other app's rules are never named here.
            Run("netsh", $"advfirewall firewall delete rule name=\"{Program.FirewallRule}\"");
            AddRule("TCP", Program.HttpPort);
            AddRule("UDP", Program.DiscoveryPort);
            // VLC's web remote needs inbound too: one Private-networks-only rule for vlc.exe.
            var vlc = Movies.FindVlc();
            if (vlc != null)
            {
                Run("netsh", $"advfirewall firewall delete rule name=\"{Program.VlcFirewallRule}\"");
                var vcode = Run("netsh", $"advfirewall firewall add rule name=\"{Program.VlcFirewallRule}\" dir=in action=allow program=\"{vlc}\" profile=private remoteip=localsubnet");
                if (vcode != 0) Console.Error.WriteLine("The VLC firewall rule could not be added (the phone may still reach VLC if Windows already allows it).");
                else
                {
                    // Ownership marker: a future elevated cleanup may remove only what we
                    // created - never a rule another app (or Windows' own prompt) owns.
                    try { File.WriteAllText(VlcRuleMarkerPath(dataDir), vlc + "\n" + DateTime.UtcNow.ToString("O")); } catch { }
                }
            }
            else Console.Error.WriteLine("VLC is not installed - its firewall rule was skipped.");
            // Report, never touch: any broad vlc.exe rules from elsewhere (all networks, or
            // all programs) are listed for the helper log. Zephyr VLC Remote only adds its own
            // Private-only rule above.
            try { File.WriteAllText(VlcInventoryPath(dataDir), InventoryVlcRules()); } catch { }
            // The listen permission: replace only what is ours (already correct, or the
            // broad Everyone grant a 1.x build wrote). A reservation belonging to another
            // app or another Windows user is NEVER overwritten - that is their port.
            switch (aclOwner)
            {
                case AclOwnership.Ours:
                    break; // already exactly right
                case AclOwnership.None:
                case AclOwnership.Legacy:
                    Run("netsh", "http delete urlacl url=http://+:" + Program.HttpPort + "/");
                    var acl = Run("netsh", "http add urlacl url=http://+:" + Program.HttpPort + "/ sddl=D:(A;;GX;;;" + sid + ")");
                    if (acl != 0 || !UrlAclExists())
                    {
                        Program.Log("firewall setup: the listen permission could not be added (netsh exit " + acl + ").");
                        Console.Error.WriteLine("The listen permission could not be added.");
                        return 3;
                    }
                    break;
            }
            // Verify the reservation really names the intended user, not just the port:
            // a stale or differently-scoped reservation is the exact thing that leaves
            // the System-attributed TCP rule guarded by the wrong account.
            if (!UrlAclNamesUser(sid))
            {
                Program.Log("firewall setup: the listen permission on " + Program.HttpPort +
                    " does not name SID " + sid + " after setup.");
                Console.Error.WriteLine("The listen permission could not be verified.");
                return 3;
            }
            Program.Log("firewall setup: rules added (TCP System/" + Program.HttpPort + ", UDP exe/" +
                Program.DiscoveryPort + "), listen permission scoped to SID " + sid + ".");
            if (!RuleExists())
            {
                Console.Error.WriteLine("The firewall rule could not be verified after adding it.");
                return 2;
            }
            // The marker is written by the UNELEVATED copy after it verifies the rules
            // itself: an elevated write would land in the APPROVING admin's profile when
            // someone else's credentials answered the UAC prompt (round-6 N7).
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Firewall setup failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Explains the prompt (dark dialog), relaunches elevated for the one-time
    /// setup, and returns true only when the rules + listen permission are really in
    /// place. Any decline or failure shows ONE dark note and returns false so the caller
    /// leaves cleanly instead of falling through to a misleading listen error.</summary>
    public static bool ExplainAndRelaunch()
    {
        var ok = ConfirmDialog.Ask(null,
            "One Windows permission, then setup is done. " +
            "Zephyr VLC Remote needs to let your phone reach this PC on your home network, so Windows will now ask for administrator permission once. " +
            "It adds three firewall rules for home (Private) networks only - one for the phone connection, one for phone discovery, and one that opens VLC's own remote on Private networks - plus the listen permission for the phone connection.",
            "Continue");
        if (!ok)
        {
            NoteDialog.Show(null, "No problem - setup will be offered again next time you open Zephyr VLC Remote.");
            return false;
        }

        int? exitCode = null;
        var conflict = false;
        try
        {
            var exe = Environment.ProcessPath!;
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
            var psi = new ProcessStartInfo(exe, "--install-firewall \"" + sid + "\" \"" + Program.DataDir + "\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("The elevated setup did not start.");
            if (!p.WaitForExit(60_000)) throw new InvalidOperationException("Setup timed out.");
            exitCode = p.ExitCode;
            conflict = exitCode == 4;
            // Verify the ACTUAL shape and ACL owner, not name/port existence: a rule
            // the migration failed to rewrite must not pass here (round-8).
            LastReadFailed = false;
            var verified = RulesHaveCurrentShape() && UrlAclNamesCurrentUser();
            if (exitCode != 0 || !verified)
            {
                if (LastReadFailed)
                    // Elevation cannot fix an unreadable firewall/ACL API: say so, and
                    // do NOT re-elevate or retry in a loop (round-9 N9-8).
                    NoteDialog.Show(null, "Windows Firewall settings could not be read on this PC, so setup cannot check its own work. Zephyr VLC Remote will close now; a restart of Windows usually fixes the read.");
                throw new InvalidOperationException("Setup could not be verified.");
            }
            return true;
        }
        catch
        {
            if (conflict)
                NoteDialog.Show(null,
                    "Something else on this PC has already reserved the network port Zephyr VLC Remote needs (" + Program.HttpPort + "), and it is not one of ours, so setup stopped without changing it. Zephyr VLC Remote will close now; freeing that port needs whoever manages this PC.");
            else if (!LastReadFailed)
                // The user said no at the UAC prompt, or setup failed partway. Say what is
                // true in every case: setup did not finish (rules may or may not exist).
                // A read failure already said its own plainer note above (round-9 N9-8).
                NoteDialog.Show(null,
                    "Setup did not finish, so the phone cannot reach this PC yet. Zephyr VLC Remote will now close and offer to set up again next time you open it.");
            return false;
        }
    }

    /// <summary>One firewall rule's shape, read through the Windows Firewall COM API
    /// (HNetCfg.FwPolicy2). Rule DATA is language-independent - unlike netsh's display
    /// text, which is localized and made the old text parse re-ask for setup on every
    /// launch on a non-English PC (round-8 N-1).</summary>
    internal sealed record RuleShape(string Name, int Protocol, string LocalPorts, int Direction,
        bool Enabled, int Profiles, string RemoteAddresses, string ApplicationName, int Action);

    /// <summary>True when the last firewall/ACL READ failed at the API level (distinct
    /// from "read fine, not ours"): the user gets a plain could-not-read note and setup
    /// does NOT re-elevate, because admin cannot fix an unreadable API (round-9 N9-8).</summary>
    internal static bool LastReadFailed;

    /// <summary>All rules carrying any of [names], via the firewall COM API. Null when
    /// the API itself failed (treated as "not current" - setup is re-offered, never skipped).</summary>
    internal static List<RuleShape>? ReadRulesByName(params string[] names)
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) { LastReadFailed = true; return null; }
            dynamic policy = Activator.CreateInstance(policyType)!;
            var list = new List<RuleShape>();
            foreach (dynamic r in policy.Rules)
            {
                string name;
                try { name = (string)r.Name; } catch { continue; }
                if (!names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new RuleShape(
                    name,
                    (int)r.Protocol,
                    (string)(r.LocalPorts ?? "") ?? "",
                    (int)r.Direction,
                    (bool)r.Enabled,
                    (int)r.Profiles,
                    (string)(r.RemoteAddresses ?? "") ?? "",
                    (string)(r.ApplicationName ?? "") ?? "",
                    (int)r.Action));
            }
            return list;
        }
        catch (Exception ex)
        {
            LastReadFailed = true;
            Program.Log("firewall shape check: the Windows Firewall API could not be read (" + ex.GetType().Name + "); treating setup as not done.");
            return null;
        }
    }

    /// <summary>One rule carries the required shape: direction In, enabled, Private
    /// ONLY (any Domain/Public bit fails), LocalSubnet ONLY ("*" fails), the exact
    /// port, and the exact program (System for the http.sys TCP rule, this exe for UDP).</summary>
    internal static bool RuleMatches(RuleShape r, int protocol, int port, string program)
    {
        if (r.Protocol != protocol) return false;
        if (r.Action != 1) return false; // NET_FW_ACTION_ALLOW - a same-named BLOCK rule is not our shape (round-9 N9-4)
        if (r.Direction != 1 || !r.Enabled) return false; // NET_FW_RULE_DIR_IN
        if (r.Profiles != 2) return false;                // NET_FW_PROFILE2_PRIVATE only
        if (!string.Equals(r.RemoteAddresses.Trim(), "LocalSubnet", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(r.LocalPorts.Trim(), port.ToString(), StringComparison.Ordinal)) return false; // exactly our port - '47823,80' opens more than our shape (round-10 N10-5)
        return string.Equals(r.ApplicationName.Trim(), program, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when BOTH helper rules exist AND carry the current shape: TCP
    /// program=System + LocalSubnet + Private (http.sys owns the listener in the kernel,
    /// so an exe-scoped TCP rule would never match), UDP exe-scoped + LocalSubnet +
    /// Private (the discovery socket lives in this process). EVERY rule under our name
    /// must match (round-8 N-3: a stale broader rule beside a correct one still fails)
    /// and the legacy 1.x rule must be entirely gone. Name-only checks pass on the old
    /// 1.x shape, which is exactly the upgrade bug this guards against.</summary>
    public static bool RulesHaveCurrentShape()
    {
        return RulesHaveCurrentShape(ReadRulesByName(Program.FirewallRule, Program.LegacyFirewallRule), Environment.ProcessPath ?? "");
    }

    internal static bool RulesHaveCurrentShape(List<RuleShape>? rules, string exePath)
    {
        if (rules == null) return false;
        if (rules.Any(r => string.Equals(r.Name, Program.LegacyFirewallRule, StringComparison.OrdinalIgnoreCase))) return false;
        var ours = rules.Where(r => string.Equals(r.Name, Program.FirewallRule, StringComparison.OrdinalIgnoreCase)).ToList();
        var tcp = ours.Where(r => r.Protocol == 6).ToList();
        var udp = ours.Where(r => r.Protocol == 17).ToList();
        if (tcp.Count == 0 || udp.Count == 0 || tcp.Count + udp.Count != ours.Count) return false;
        return tcp.All(r => RuleMatches(r, 6, Program.HttpPort, "System"))
            && udp.All(r => RuleMatches(r, 17, Program.DiscoveryPort, exePath));
    }

    /// <summary>True when the port's reservation names the CURRENT user (read-only check
    /// the unelevated copy can run: netsh http show urlacl needs no admin).</summary>
    public static bool UrlAclNamesCurrentUser()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        return sid != null && UrlAclNamesUser(sid);
    }

    /// <summary>The port's reservation record straight from http.sys (typed API, no
    /// netsh display text - the "SDDL:" label is localized and a regex on it fails
    /// OPEN; round-9 N9-3). Found=false: no reservation for the exact URL. Sddl null
    /// on any read error: existing-but-unreadable, i.e. Foreign - we never delete
    /// what we cannot read.</summary>
    private static (bool Found, string? Sddl) QueryUrlAcl(string urlPrefix)
    {
        // http.h lifecycle: HttpInitialize with HTTP_INITIALIZE_CONFIG is required
        // before the query, HttpTerminate after.
        uint initRc = HttpInitialize(new HttpApiVersion { Major = 1, Minor = 0 }, HttpInitializeConfig, IntPtr.Zero);
        if (initRc != 0)
        {
            LastReadFailed = true;
            Program.Log("http.sys init failed (win32 " + initRc + "); treating the reservation as unreadable/foreign.");
            return (true, null);
        }
        try
        {
            var query = new HttpServiceConfigUrlAclQuery
            {
                QueryDesc = HttpServiceConfigQueryExact,
                KeyDesc = new HttpServiceConfigUrlAclKey { UrlPrefix = urlPrefix },
                Token = 0,
            };
            // The record's strings live inside the output buffer past the struct, so
            // size for content: on ERROR_INSUFFICIENT_BUFFER/ERROR_MORE_DATA the API
            // reports the needed size through pReturnLength (http.h). Any other error
            // is a READ error - never "no reservation" (round-9 N9-3 fail-closed,
            // round-10 B10-1).
            var outSize = 256;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(outSize);
                try
                {
                    uint rc = HttpQueryServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclQueryId,
                        ref query, System.Runtime.InteropServices.Marshal.SizeOf(query), buf, outSize,
                        out int needed, IntPtr.Zero);
                    // No matching record: ERROR_FILE_NOT_FOUND per http.h (round-10
                    // B10-1); 1168 (ERROR_NOT_FOUND) kept as the same "absent" meaning
                    // on older http.sys. Both mean NOT RESERVED - unlike every other
                    // error, which is unreadable, i.e. Foreign.
                    if (rc == ErrorFileNotFound || rc == ErrorNotFound) return (false, null);
                    if (rc == ErrorInsufficientBuffer || rc == ErrorMoreData)
                    {
                        if (needed > outSize) { outSize = needed; continue; }
                        LastReadFailed = true;
                        return (true, null);
                    }
                    if (rc != 0)
                    {
                        LastReadFailed = true;
                        Program.Log("urlacl query failed (win32 " + rc + "); treating the reservation as unreadable/foreign.");
                        return (true, null);
                    }
                    var set = System.Runtime.InteropServices.Marshal.PtrToStructure<HttpServiceConfigUrlAclSet>(buf);
                    return (true, set.Param.StringSecurityDescriptor);
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }
            LastReadFailed = true;
            return (true, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.MarshalDirectiveException)
        {
            LastReadFailed = true;
            return (true, null);
        }
        finally { HttpTerminate(HttpInitializeConfig, IntPtr.Zero); }
    }

    private const int HttpServiceConfigUrlAclQueryId = 2; // HttpServiceConfigUrlAclInfo
    private const int HttpServiceConfigQueryExact = 0;
    private const uint HttpInitializeConfig = 0x2;
    private const uint ErrorFileNotFound = 2;
    private const uint ErrorNotFound = 1168;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorMoreData = 234;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct HttpServiceConfigUrlAclKey
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string UrlPrefix;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct HttpServiceConfigUrlAclQuery
    {
        public int QueryDesc;
        public HttpServiceConfigUrlAclKey KeyDesc;
        public int Token;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct HttpServiceConfigUrlAclParam
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string StringSecurityDescriptor;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct HttpServiceConfigUrlAclSet
    {
        public HttpServiceConfigUrlAclKey KeyDesc;
        public HttpServiceConfigUrlAclParam Param;
    }

    [System.Runtime.InteropServices.DllImport("httpapi.dll", ExactSpelling = true)]
    private static extern uint HttpQueryServiceConfiguration(IntPtr serviceHandle, int configId,
        ref HttpServiceConfigUrlAclQuery input, int inputLength, IntPtr output, int outputLength,
        out int returnLength, IntPtr overlapped);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct HttpApiVersion { public ushort Major; public ushort Minor; }

    [System.Runtime.InteropServices.DllImport("httpapi.dll", ExactSpelling = true)]
    private static extern uint HttpInitialize(HttpApiVersion version, uint flags, IntPtr reserved);

    [System.Runtime.InteropServices.DllImport("httpapi.dll", ExactSpelling = true)]
    private static extern uint HttpTerminate(uint flags, IntPtr reserved);

    /// <summary>Pure classification of one reservation SDDL: us (an exact per-user grant
    /// for [sid]), a legacy broad grant our 1.x builds wrote (Everyone, nothing else),
    /// nobody, or something else - another app or another Windows user, which must
    /// never be overwritten. EXACT whole-descriptor equality on both sides: a multi-ACE
    /// descriptor that merely CONTAINS our user or an Everyone ACE is foreign (round-9
    /// T4), and SDDL aliases (WD, LA, SY) normalize through the parser, never substrings.</summary>
    internal static AclOwnership ClassifySddl(string? sddl, string? sid)
    {
        if (string.IsNullOrWhiteSpace(sddl)) return AclOwnership.Foreign;
        string normalized;
        try
        {
            normalized = new System.Security.AccessControl.RawSecurityDescriptor(sddl)
                .GetSddlForm(System.Security.AccessControl.AccessControlSections.All);
        }
        catch { return AclOwnership.Foreign; }
        if (sid != null)
        {
            try
            {
                var ours = new System.Security.AccessControl.RawSecurityDescriptor("D:(A;;GX;;;" + sid + ")")
                    .GetSddlForm(System.Security.AccessControl.AccessControlSections.All);
                if (normalized == ours) return AclOwnership.Ours;
            }
            catch { }
        }
        try
        {
            var legacy = new System.Security.AccessControl.RawSecurityDescriptor("D:(A;;GX;;;WD)")
                .GetSddlForm(System.Security.AccessControl.AccessControlSections.All);
            if (normalized == legacy) return AclOwnership.Legacy;
        }
        catch { }
        return AclOwnership.Foreign;
    }

    /// <summary>Classifies the port's LIVE reservation. A reservation that exists but
    /// cannot be read or parsed is Foreign, never None (round-9 N9-3: the netsh-text
    /// version returned None on an unparsed block and setup would have DELETED a
    /// reservation it could not read).</summary>
    internal static AclOwnership ClassifyUrlAcl(string? sid)
    {
        var (found, sddl) = QueryUrlAcl("http://+:" + Program.HttpPort + "/");
        if (!found) return AclOwnership.None;
        return ClassifySddl(sddl, sid);
    }

    private static string VlcInventoryPath(string? dataDir = null) =>
        Path.Combine(dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zephyr VLC Remote"), "vlc-firewall-inventory.txt");

    /// <summary>One line per existing vlc.exe firewall rule: name, profile, action, enabled, path.</summary>
    private static string InventoryVlcRules()
    {
        var psi = new ProcessStartInfo("powershell",
            """
            -NoProfile -NonInteractive -Command "Get-NetFirewallRule -ErrorAction SilentlyContinue | ForEach-Object { $af = $_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue; if ($af -and $af.Program -like '*vlc.exe') { $_.DisplayName+'|'+$_.Profile+'|'+$_.Action+'|'+$_.Enabled+'|'+$af.Program } }"
            """)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return ""; }
        return p.StandardOutput.ReadToEnd();
    }

    /// <summary>Log the saved vlc.exe rule inventory (called once after setup ran).</summary>
    public static void LogVlcRuleInventory()
    {
        try
        {
            var path = VlcInventoryPath();
            if (!File.Exists(path)) return;
            var lines = File.ReadAllText(path).Trim();
            if (lines.Length == 0) { Program.Log("vlc.exe firewall rules on this PC: none besides what Zephyr VLC Remote adds."); return; }
            foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Program.Log("vlc.exe firewall rule present: " + line.Trim());
        }
        catch { }
    }

    private static string VlcRuleMarkerPath(string? dataDir = null) =>
        Path.Combine(dataDir ?? Program.DataDir, "vlc-firewall-rule-owned.txt");

    private static void AddRule(string protocol, int port)
    {
        // http.sys owns inbound HTTP in the KERNEL: Windows Firewall attributes the TCP
        // listener to the System binary, not this exe (Microsoft documents this for
        // http.sys/IIS; System.Net.HttpListener runs on http.sys). A rule naming our exe
        // would never match and the phone would be dropped on a real LAN - loopback CI
        // cannot see that. So TCP 8492 is program=System + LocalSubnet + Private, and
        // the per-user URL ACL becomes the limit on who may take the port. The UDP
        // discovery socket lives in THIS process, so its rule stays program-scoped.
        Run("netsh", $"advfirewall firewall delete rule name=\"{Program.FirewallRule}\" protocol={protocol} localport={port}");
        string args;
        if (protocol == "TCP")
            args = $"advfirewall firewall add rule name=\"{Program.FirewallRule}\" dir=in action=allow protocol=TCP localport={port} program=System profile=private remoteip=localsubnet";
        else
            args = $"advfirewall firewall add rule name=\"{Program.FirewallRule}\" dir=in action=allow protocol=UDP localport={port} program=\"{Environment.ProcessPath ?? ""}\" profile=private remoteip=localsubnet";
        var code = Run("netsh", args);
        if (code != 0) throw new InvalidOperationException($"netsh add rule exited {code}");
    }

    /// <summary>Remove what setup added: both helper rules and, when it is ours, the
    /// listen permission. Runs elevated at uninstall. [userSid] is the UNINSTALLING
    /// user's SID (the unelevated uninstaller passes it): the ACL is deleted only when
    /// it names that user or is the broad 1.x Everyone grant - another user's or
    /// another app's reservation stays. The vlc.exe rule is deliberately left: it may
    /// be shared, and only the ownership marker proves it ours.</summary>
    public static int RemoveElevated(string? userSid)
    {
        string? sid = null;
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            try { sid = new System.Security.Principal.SecurityIdentifier(userSid).Value; }
            catch { sid = null; }
        }
        if (sid == null)
        {
            // whoami gave the uninstaller nothing: without the user's SID our per-user
            // grant cannot be told from another user's, so the reservation STAYS
            // (round-8 N-11: never misclassify an empty SID as a foreign owner).
            Console.Error.WriteLine("The uninstalling user's account could not be determined, so the listen permission on " + Program.HttpPort + " was left in place.");
            return 0;
        }
        // Classify the reservation BEFORE touching any rule (round-9 N9-5): when it is
        // another app's or another Windows user's, their install likely owns the
        // machine-wide rules too - deleting the rules out from under them would break
        // their copy, so nothing is touched.
        var owner = ClassifyUrlAcl(sid);
        if (owner == AclOwnership.Foreign)
        {
            Console.Error.WriteLine("The listen permission on " + Program.HttpPort + " belongs to another app or Windows user; the firewall rules were left in place too.");
            return 0;
        }
        Run("netsh", $"advfirewall firewall delete rule name=\"{Program.FirewallRule}\"");
        if (owner == AclOwnership.Ours || owner == AclOwnership.Legacy)
            Run("netsh", "http delete urlacl url=http://+:" + Program.HttpPort + "/");
        return 0;
    }

    internal static bool UrlAclExists()
    {
        return QueryUrlAcl("http://+:" + Program.HttpPort + "/").Found;
    }

    /// <summary>True when the port's reservation is EXACTLY a per-user grant for this
    /// SID, via the typed query + normalized whole-descriptor equality. A substring is
    /// never enough (round-9 T4: ...-1001 vs ...-10012, multi-ACE descriptors).</summary>
    internal static bool UrlAclNamesUser(string sid)
    {
        return ClassifyUrlAcl(sid) == AclOwnership.Ours;
    }


    internal static bool RuleExists()
    {
        var rules = ReadRulesByName(Program.FirewallRule);
        return rules != null && rules.Count > 0;
    }

    private static int Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        // Drain both streams: an unread redirected pipe can fill and block the child
        // (and therefore WaitForExit) forever (round-8 N-13).
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Task.WaitAll(stdout, stderr);
        return p.ExitCode;
    }
}
