using System.Diagnostics;

namespace ZephyrVlcRemote;

/// <summary>
/// The one-time firewall step. Windows needs admin rights to add a firewall rule and the
/// listen permission, so the helper restarts itself once with the UAC prompt, adds a
/// Private-networks-only rule, checks both are really there, and leaves a marker so it
/// never asks again.
/// </summary>
internal static class FirewallSetup
{
    /// <summary>Runs inside the elevated copy. Returns the process exit code.
    /// [userSid] is the UNelevated user's SID: the listen permission is scoped to that
    /// account, not everyone. [userDataDir] is that user's data folder, so the markers
    /// land where the user's copy reads them even when another admin approved the UAC.
    /// Both fall back to this process's own identity/folder for direct runs.</summary>
    public static int RunElevated(string? userSid, string? userDataDir)
    {
        try
        {
            var sid = userSid ?? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            var dataDir = userDataDir ?? Program.DataDir;
            Run("netsh", $"advfirewall firewall delete rule name=\"{Program.LegacyFirewallRule}\""); // 1.x installs
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
            // Let the helper (a normal, non-admin app) listen on its port. Scoped to the
            // installing user's SID - after the System-attributed TCP rule this ACL is the
            // thing that limits who may take the port at all.
            Run("netsh", "http delete urlacl url=http://+:" + Program.HttpPort + "/");
            var acl = Run("netsh", "http add urlacl url=http://+:" + Program.HttpPort + "/ sddl=D:(A;;GX;;;" + sid + ")");
            if (acl != 0 || !UrlAclExists())
            {
                Console.Error.WriteLine("The listen permission could not be added.");
                return 3;
            }
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
        if (!ok) return false;

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
            if (p == null || !p.WaitForExit(60_000) || p.ExitCode != 0 || !RuleExists() || !UrlAclExists())
                throw new InvalidOperationException("Setup could not be verified.");
            return true;
        }
        catch
        {
            // The user said no at the UAC prompt, or setup failed. Without the rule and
            // listen permission the helper cannot accept connections, so it closes and
            // offers again next launch - no second, misleading error box after this one.
            NoteDialog.Show(null,
                "No firewall rule was added, so the phone cannot reach this PC yet. Zephyr VLC Remote will now close and offer to set up again next time you open it.");
            return false;
        }
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

    /// <summary>Remove what setup added: both helper rules and the listen permission.
    /// Runs elevated at uninstall (or from a future in-app cleanup). The vlc.exe rule is
    /// deliberately left: it may be shared, and only the ownership marker proves it ours.</summary>
    public static int RemoveElevated()
    {
        Run("netsh", $"advfirewall firewall delete rule name=\"{Program.FirewallRule}\"");
        Run("netsh", "http delete urlacl url=http://+:" + Program.HttpPort + "/");
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
