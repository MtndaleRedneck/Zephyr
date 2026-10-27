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
            switch (ClassifyUrlAcl(sid))
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
                case AclOwnership.Foreign:
                    Program.Log("firewall setup: port " + Program.HttpPort + " is reserved by another app or Windows user; left untouched.");
                    Console.Error.WriteLine("The network port Zephyr VLC Remote needs is reserved for another app or another Windows user on this PC.");
                    return 4;
            }
            // Verify the reservation really names the intended user, not just the port:
            // a stale or differently-scoped reservation is the exact thing that leaves
            // the System-attributed TCP rule guarded by the wrong account.
            if (!UrlAclNamesUser(sid))
            {
                Program.Log("firewall setup: the listen permission on " + Program.HttpPort +
                    " does not name SID " + sid + " after setup. Actual reservation: " + UrlAclBlock());
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
            if (exitCode != 0 || !RuleExists() || !UrlAclExists())
                throw new InvalidOperationException("Setup could not be verified.");
            return true;
        }
        catch
        {
            if (conflict)
                NoteDialog.Show(null,
                    "Something else on this PC has already reserved the network port Zephyr VLC Remote needs (" + Program.HttpPort + "), and it is not one of ours, so setup stopped without changing it. Zephyr VLC Remote will close now; freeing that port needs whoever manages this PC.");
            else
                // The user said no at the UAC prompt, or setup failed partway. Say what is
                // true in every case: setup did not finish (rules may or may not exist).
                NoteDialog.Show(null,
                    "Setup did not finish, so the phone cannot reach this PC yet. Zephyr VLC Remote will now close and offer to set up again next time you open it.");
            return false;
        }
    }

    /// <summary>True when BOTH helper rules exist AND carry the current shape: TCP
    /// program=System + LocalSubnet + Private (http.sys owns the listener in the kernel,
    /// so an exe-scoped TCP rule would never match), UDP exe-scoped + LocalSubnet +
    /// Private (the discovery socket lives in this process). Name-only checks pass on
    /// the old 1.x shape, which is exactly the upgrade bug this guards against.</summary>
    public static bool RulesHaveCurrentShape()
    {
        return RuleBlockMatches("TCP", Program.HttpPort, "System")
            && RuleBlockMatches("UDP", Program.DiscoveryPort, Environment.ProcessPath ?? "");
    }

    private static bool RuleBlockMatches(string protocol, int port, string program)
    {
        var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{Program.FirewallRule}\" verbose")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        foreach (var block in output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None))
        {
            if (!block.Contains("Rule Name:")) continue;
            if (!FieldIs(block, "Protocol", protocol)) continue;
            if (!FieldIs(block, "LocalPort", port.ToString())) continue;
            if (!FieldIs(block, "Program", program)) continue;
            if (!FieldIs(block, "Direction", "In")) continue;
            if (!FieldIs(block, "Enabled", "Yes")) continue;
            var profiles = Field(block, "Profiles");
            if (profiles == null || profiles.Contains("Any") || profiles.Contains("Public")
                || profiles.Contains("Domain") || !profiles.Contains("Private")) continue;
            var remote = Field(block, "RemoteIP");
            if (remote == null || !(remote.Contains("Local subnet") || remote.Contains("LocalSubnet") || remote.Contains("localsubnet"))) continue;
            return true;
        }
        return false;
    }

    private static string? Field(string block, string name)
    {
        foreach (var line in block.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) return t.Substring(name.Length + 1).Trim();
        }
        return null;
    }

    private static bool FieldIs(string block, string name, string value) =>
        string.Equals(Field(block, name), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the port's reservation names the CURRENT user (read-only check
    /// the unelevated copy can run: netsh http show urlacl needs no admin).</summary>
    public static bool UrlAclNamesCurrentUser()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        return sid != null && UrlAclNamesUser(sid);
    }

    /// <summary>Who the port's URL reservation belongs to: us (an exact per-user grant
    /// for [sid]), a legacy broad grant our 1.x builds wrote (Everyone, nothing else),
    /// nobody, or something else - another app or another Windows user, which must
    /// never be overwritten. Both sides are compared through parsed security
    /// descriptors: netsh normalizes well-known accounts to SDDL aliases when STORING
    /// (RID-500 admin comes back "LA", Everyone "WD"), so substring checks mislead.</summary>
    internal static AclOwnership ClassifyUrlAcl(string? sid)
    {
        var block = UrlAclBlock();
        var m = System.Text.RegularExpressions.Regex.Match(block, @"SDDL:\s*(\S+)");
        if (!m.Success) return AclOwnership.None;
        string normalized;
        try
        {
            normalized = new System.Security.AccessControl.RawSecurityDescriptor(m.Groups[1].Value)
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
        Run("netsh", $"advfirewall firewall delete rule name=\"{Program.FirewallRule}\"");
        string? sid = null;
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            try { sid = new System.Security.Principal.SecurityIdentifier(userSid).Value; }
            catch { sid = null; }
        }
        var owner = ClassifyUrlAcl(sid);
        if (owner == AclOwnership.Ours || owner == AclOwnership.Legacy)
            Run("netsh", "http delete urlacl url=http://+:" + Program.HttpPort + "/");
        else if (owner == AclOwnership.Foreign)
            Console.Error.WriteLine("The listen permission on " + Program.HttpPort + " belongs to another app or Windows user; left in place.");
        return 0;
    }

    internal static bool UrlAclExists()
    {
        var psi = new ProcessStartInfo("netsh", "http show urlacl")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Contains(":" + Program.HttpPort + "/");
    }

    /// <summary>True when the port's reservation SDDL names this SID. netsh normalizes
    /// well-known accounts to SDDL aliases when STORING the reservation (the runner's
    /// RID-500 admin comes back as "LA", Everyone as "WD"), so a substring match on the
    /// raw SID misses correct reservations; both sides are compared through a parsed
    /// security descriptor instead.</summary>
    internal static bool UrlAclNamesUser(string sid)
    {
        var block = UrlAclBlock();
        var m = System.Text.RegularExpressions.Regex.Match(block, @"SDDL:\s*(\S+)");
        if (!m.Success) return false;
        try
        {
            var actual = new System.Security.AccessControl.RawSecurityDescriptor(m.Groups[1].Value)
                .GetSddlForm(System.Security.AccessControl.AccessControlSections.All);
            var expected = new System.Security.AccessControl.RawSecurityDescriptor("D:(A;;GX;;;" + sid + ")")
                .GetSddlForm(System.Security.AccessControl.AccessControlSections.All);
            return actual == expected;
        }
        catch { return block.Contains(sid); }
    }

    private static string UrlAclBlock()
    {
        var psi = new ProcessStartInfo("netsh", "http show urlacl")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        foreach (var block in output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None))
            if (block.Contains(":" + Program.HttpPort + "/")) return block.Trim();
        return "(no reservation for port " + Program.HttpPort + ")";
    }

    internal static bool RuleExists()
    {
        var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{Program.FirewallRule}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Contains(Program.FirewallRule) && !output.Contains("No rules match");
    }

    private static int Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
