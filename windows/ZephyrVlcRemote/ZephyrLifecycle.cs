using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
#if ZEPHYR_PLAYER
using ZephyrPlayer;
#else
using ZephyrVlcRemote;
#endif

// Shared by Zephyr VLC Remote and Zephyr Player (the Player links this file, as it does HelperTls.cs).
// Pattern ported from the BingeMote lifecycle at fdb4bbc; NOT proven here: not compiled, not run on Windows.
namespace ZephyrLifecycle;

internal static class LifeLog
{
    internal static Action<string> Sink = m => Console.Error.WriteLine(m);
    internal static void Log(string m) { try { Sink(m); } catch { } }
}

internal enum JournalState { Absent, Present, Corrupt }

/// <summary>A firewall rule this build created: its unique Name (random key chosen at creation, never reused), the DisplayName, and the property line read back right after creation.</summary>
internal sealed record RuleEntry(string Key, string DisplayName, string Props);
/// <summary>Recovery evidence only: a create under this fresh Name was attempted but could not be confirmed. NEVER delete authority.</summary>
internal sealed record UncertainEntry(string Key, string DisplayName, string Outcome);
/// <summary>The per-user relaunch task. Verified=false: registered but the read-back could not be confirmed; kept as history, never refreshed or deleted on that basis.</summary>
internal sealed record WatchdogEntry(string Task, string Sid, string Sddl, string Exe, bool Verified, string? Note = null);
/// <summary>The https:// listen permission this build ADDED (the reservation did not exist, the add succeeded, and the live descriptor read back exactly the intended per-user grant). Uninstall deletes it only while the live descriptor is still equivalent.</summary>
internal sealed record UrlAclEntry(string Url, string Sddl);
internal sealed record LifecycleJournal(int Version, List<RuleEntry> Rules, WatchdogEntry? Watchdog, List<UncertainEntry> Uncertain, UrlAclEntry? UrlAcl = null);

internal static class SddlUtil
{
    /// <summary>ACE-set equality of two SDDL strings (order and aliases such as SY/BA/WD do not matter). Anything unparseable is unequal.</summary>
    internal static bool Equivalent(string a, string b)
    {
        try
        {
            static HashSet<string>? Aces(string s)
            {
                var d = new System.Security.AccessControl.RawSecurityDescriptor(s).DiscretionaryAcl;
                if (d == null) return null;
                return d.Cast<System.Security.AccessControl.GenericAce>().Select(x => x is System.Security.AccessControl.CommonAce c
                    ? c.AceType + "|" + c.SecurityIdentifier.Value + "|" + c.AccessMask + "|" + c.AceFlags
                    : "other|" + x.AceType).ToHashSet();
            }
            var x1 = Aces(a); var x2 = Aces(b);
            return x1 != null && x2 != null && x1.SetEquals(x2);
        }
        catch { return false; }
    }
    internal static string PerUserListenGrant(string sid) => "D:(A;;GX;;;" + sid + ")";
}

/// <summary>What the elevated setup itself created. An entry is written only after the object was created by THIS run (name was absent, create succeeded) and read back exact.
/// Uninstall removes only journaled objects, and only while the live object still equals what was journaled. Admin-vs-admin replacement is NOT prevented.</summary>
internal static class LifecycleJournalStore
{
    internal const string KeyPattern = @"\AZephyr-[0-9a-f]{32}\z";
    internal static string FilePath => Path.Combine(ProtectedFile.Dir, "lifecycle.json");

    internal static bool IsValid(LifecycleJournal? j) =>
        j != null && j.Version == 1 && j.Rules != null && j.Uncertain != null
        && j.Rules.All(e => e != null && Regex.IsMatch(e.Key ?? "", KeyPattern) && !string.IsNullOrEmpty(e.DisplayName) && !string.IsNullOrEmpty(e.Props))
        && j.Uncertain.All(u => u != null && Regex.IsMatch(u.Key ?? "", KeyPattern) && !string.IsNullOrEmpty(u.Outcome))
        && (j.UrlAcl == null || (Regex.IsMatch(j.UrlAcl.Url ?? "", @"\Ahttps://\+:\d{1,5}/\z") && !string.IsNullOrEmpty(j.UrlAcl.Sddl)))
        && (j.Watchdog == null || (WatchdogIds.SidAcceptable(j.Watchdog.Sid) && j.Watchdog.Task == WatchdogIds.TaskName));

    internal static (JournalState state, LifecycleJournal? journal) Read(string? path = null)
    {
        var p = path ?? FilePath;
        try
        {
            if (!File.Exists(p)) return (JournalState.Absent, null);
            if (path == null && !ProtectedFile.DirTrusted(ProtectedFile.Dir)) return (JournalState.Corrupt, null);
            var j = JsonSerializer.Deserialize<LifecycleJournal>(File.ReadAllText(p));
            return IsValid(j) ? (JournalState.Present, j) : (JournalState.Corrupt, null);
        }
        catch { return (JournalState.Corrupt, null); }
    }

    internal static bool Merge(IEnumerable<RuleEntry> rules, WatchdogEntry? watchdog, IEnumerable<UncertainEntry>? uncertain = null, UrlAclEntry? urlAcl = null)
    {
        try
        {
            var (st, cur) = Read();
            if (st == JournalState.Corrupt) { LifeLog.Log("lifecycle journal is unreadable or untrusted; not updated. New objects stay un-journaled (never auto-removed)."); return false; }
            var merged = new LifecycleJournal(1,
                (cur?.Rules ?? new List<RuleEntry>()).Concat(rules).DistinctBy(e => e.Key).ToList(),
                watchdog ?? cur?.Watchdog,
                (cur?.Uncertain ?? new List<UncertainEntry>()).Concat(uncertain ?? Array.Empty<UncertainEntry>()).DistinctBy(u => u.Key).ToList(),
                urlAcl ?? cur?.UrlAcl);
            ProtectedFile.WriteAtomic(FilePath, JsonSerializer.Serialize(merged));
            return true;
        }
        catch (Exception ex) { LifeLog.Log("lifecycle journal not written: " + ex.Message); return false; }
    }

    internal static void Replace(LifecycleJournal j)
    {
        try
        {
            if (j.Rules.Count == 0 && j.Watchdog == null && j.Uncertain.Count == 0 && j.UrlAcl == null) { File.Delete(FilePath); return; }
            ProtectedFile.WriteAtomic(FilePath, JsonSerializer.Serialize(j));
        }
        catch (Exception ex) { LifeLog.Log("lifecycle journal not updated: " + ex.Message); }
    }
}

internal static class WatchdogIds
{
    internal const string TaskName = "Zephyr VLC Remote Watchdog (installed)";
    internal static bool SidAcceptable(string? sid) =>
        sid != null && Regex.IsMatch(sid, @"\AS-1-(5-21-\d+-\d+-\d+-\d+|12-1-\d+-\d+-\d+-\d+)\z");
}

/// <summary>An inbound firewall rule shape. Program is "Any", "System", or an exe path. Port 0 means any port (Protocol "Any").</summary>
internal sealed record RuleSpec(string Display, string Profile, string Protocol, int Port, string Remote, string Program);
internal enum RuleState { Absent, Exact, Mismatch, Unreadable }
internal enum Found { Absent, One, Dup, Unreadable }
internal sealed record KeyRead(Found Found, string Display, string Props);

/// <summary>Create-only, unique-Name, journaled firewall rules (BingeMote pattern). Nothing is ever deleted or rewritten by DisplayName; rules found by DisplayName that are not Exact are left and reported.</summary>
internal static class ZephyrRules
{
    /// <summary>The two scoped shapes per port: Private keeps the existing LocalSubnet scope, Public is LocalSubnet4 only. No Domain, no Any remote.</summary>
    internal static RuleSpec[] ForPort(string label, string protocol, int port, string program) => new[]
    {
        new RuleSpec(label + " (Private)", "Private", protocol, port, "LocalSubnet", program),
        new RuleSpec(label + " (Public)", "Public", protocol, port, "LocalSubnet4", program),
    };

    private static string Ps(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    private static string Q(string s) => s.Replace("'", "''");

    private static (int exit, string output) RunPs(string script, int ms = 60000)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -EncodedCommand " + Ps(script)) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } return (-1, ""); }
            return (p.ExitCode, read.GetAwaiter().GetResult());
        }
        catch { return (-1, ""); }
    }

    // PropPre runs first (statements); PropExpr is one expression. [string] cast first: enum + string would try to convert the string to the enum.
    private const string PropPre =
        "$p=$x|Get-NetFirewallPortFilter; $a=$x|Get-NetFirewallAddressFilter; $ap=$x|Get-NetFirewallApplicationFilter; $sv=$x|Get-NetFirewallServiceFilter; ";
    private const string PropExpr =
        "[string]$x.Enabled+'|'+$x.Direction+'|'+$x.Action+'|'+$x.Profile+'|'+$p.Protocol+'|'+($p.LocalPort -join ',')+'|'+($p.RemotePort -join ',')+'|'+($a.RemoteAddress -join ',')+'|'+($a.LocalAddress -join ',')+'|'+$ap.Program+'|'+$sv.Service+'|'+$x.EdgeTraversalPolicy";

    /// <summary>f = the 12 property fields (enabled, direction, action, profile, protocol, local port, remote port, remote address, local address, program, service, edge traversal).</summary>
    internal static RuleState ClassifyProps(string[] f, RuleSpec s)
    {
        if (f.Length != 12) return RuleState.Mismatch;
        bool Is(string got, string want) => string.Equals(got.Trim(), want, StringComparison.OrdinalIgnoreCase);
        var ok = Is(f[0], "True") && Is(f[1], "Inbound") && Is(f[2], "Allow") && Is(f[3], s.Profile) && Is(f[4], s.Protocol)
            && Is(f[5], s.Port == 0 ? "Any" : s.Port.ToString()) && Is(f[6], "Any") && Is(f[7], s.Remote) && Is(f[8], "Any")
            && Is(f[9], s.Program) && Is(f[10], "Any") && Is(f[11], "Block");
        return ok ? RuleState.Exact : RuleState.Mismatch;
    }

    /// <summary>Absent only when the full unfiltered enumeration SUCCEEDED and no rule has that exact DisplayName. Any failure leaves Unreadable, never Absent, never triggers a create.</summary>
    internal static Dictionary<string, RuleState> ReadAll(IReadOnlyList<RuleSpec> specs)
    {
        var result = specs.ToDictionary(r => r.Display, _ => RuleState.Unreadable);
        var names = string.Join(",", specs.Select(r => "'" + Q(r.Display) + "'"));
        var script =
            "$ErrorActionPreference='Stop'; try { $all=@(Get-NetFirewallRule) } catch { 'ERR'; exit 0 }; " +
            "foreach ($n in @(" + names + ")) { try { $r=@($all | Where-Object { $_.DisplayName -ceq $n }); " +
            "if ($r.Count -eq 0) { $n+'|ABSENT'; continue }; if ($r.Count -ne 1) { $n+'|DUP'; continue }; $x=$r[0]; " + PropPre + "$n+'|ONE|'+" + PropExpr + " } catch { $n+'|ERR' } }";
        var (exit, output) = RunPs(script);
        if (exit != 0) return result;
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var cut = specs.Select(s => s.Display).FirstOrDefault(d => line.StartsWith(d + "|", StringComparison.Ordinal));
            if (cut == null) continue;
            var spec = specs.First(s => s.Display == cut);
            var rest = line.Substring(cut.Length + 1);
            if (rest == "ABSENT") result[cut] = RuleState.Absent;
            else if (rest == "DUP") result[cut] = RuleState.Mismatch;
            else if (rest.StartsWith("ONE|", StringComparison.Ordinal)) result[cut] = ClassifyProps(rest.Substring(4).Split('|'), spec);
        }
        return result;
    }

    /// <summary>Reads one rule by its actual Name key (not DisplayName).</summary>
    internal static KeyRead ReadByKey(string key)
    {
        var bad = new KeyRead(Found.Unreadable, "", "");
        if (!Regex.IsMatch(key, LifecycleJournalStore.KeyPattern)) return bad;
        var script =
            "$ErrorActionPreference='Stop'; try { $all=@(Get-NetFirewallRule) } catch { 'ERR'; exit 0 }; " +
            "try { $r=@($all | Where-Object { $_.Name -ceq '" + key + "' }); if ($r.Count -eq 0) { 'ABSENT'; exit 0 }; if ($r.Count -ne 1) { 'DUP'; exit 0 }; $x=$r[0]; " + PropPre + "'ONE|'+$x.DisplayName+'|'+" + PropExpr + " } catch { 'ERR' }";
        var (exit, output) = RunPs(script);
        if (exit != 0) return bad;
        var line = output.Trim();
        if (line == "ABSENT") return new KeyRead(Found.Absent, "", "");
        if (line == "DUP") return new KeyRead(Found.Dup, "", "");
        if (!line.StartsWith("ONE|", StringComparison.Ordinal)) return bad;
        var f = line.Split('|');
        return f.Length == 14 ? new KeyRead(Found.One, f[1], string.Join("|", f.Skip(2))) : bad;
    }

    /// <summary>For each spec: read first; Absent -> New-NetFirewallRule with a fresh unique -Name (a create under an existing Name is an error, never a replacement);
    /// Exact -> keep; anything else -> left untouched, setup fails with the reason. Journal entry only when the by-Name read-back is exactly one rule equal to the spec.</summary>
    internal static void Ensure(IReadOnlyList<RuleSpec> specs, List<RuleEntry> created, List<UncertainEntry> uncertain)
    {
        foreach (var r in specs)
        {
            var state = ReadAll(specs)[r.Display];
            if (state == RuleState.Absent)
            {
                var key = "Zephyr-" + Guid.NewGuid().ToString("N");
                var args = "-Name '" + key + "' -DisplayName '" + Q(r.Display) + "' -Direction Inbound -Action Allow -Profile " + r.Profile + " -RemoteAddress " + r.Remote;
                if (r.Protocol != "Any") args += " -Protocol " + r.Protocol;
                if (r.Port != 0) args += " -LocalPort " + r.Port;
                if (r.Program != "Any") args += " -Program '" + Q(r.Program) + "'";
                var (code, _) = RunPs("$ErrorActionPreference='Stop'; New-NetFirewallRule " + args + " | Out-Null");
                if (code != 0) { uncertain.Add(new UncertainEntry(key, r.Display, "create exited " + code)); throw new InvalidOperationException($"adding firewall rule '{r.Display}' exited {code}"); }
                state = ReadAll(specs)[r.Display];
                var rb = ReadByKey(key);
                if (state == RuleState.Exact && rb.Found == Found.One && rb.Display == r.Display) created.Add(new RuleEntry(key, r.Display, rb.Props));
                else uncertain.Add(new UncertainEntry(key, r.Display, "create returned 0 but read-back was " + state + " by display name / " + rb.Found + " by key"));
            }
            if (state != RuleState.Exact)
            {
                LifeLog.Log($"firewall setup: rule '{r.Display}' is {state} (expected exactly one enabled inbound allow rule, profile {r.Profile}, {r.Protocol} {(r.Port == 0 ? "any port" : r.Port.ToString())}, remote {r.Remote}, program {r.Program}); left untouched.");
                throw new InvalidOperationException($"firewall rule '{r.Display}' is {state}; not changed");
            }
        }
    }

    /// <summary>Runs Ensure and always journals what was created or left uncertain, even when a later rule failed. Returns false when any rule could not be made Exact or the journal could not be written for a created rule.</summary>
    internal static bool Install(IReadOnlyList<RuleSpec> specs)
    {
        var created = new List<RuleEntry>(); var uncertain = new List<UncertainEntry>();
        bool ok = true;
        try { Ensure(specs, created, uncertain); }
        catch (Exception ex) { LifeLog.Log("firewall setup: " + ex.Message); ok = false; }
        if ((created.Count > 0 || uncertain.Count > 0) && !LifecycleJournalStore.Merge(created, null, uncertain))
            LifeLog.Log("firewall rules were created but could NOT be journaled; they will never be removed automatically.");
        return ok;
    }

    /// <summary>Current means only: every expected rule is Exact by live read. It says nothing about OTHER rules; see Conflicts.</summary>
    internal static bool AllExact(IReadOnlyList<RuleSpec> specs) => ReadAll(specs).Values.All(v => v == RuleState.Exact);

    /// <summary>A rule that could widen or shadow the intended boundary: enabled inbound allow, not one this build journaled, and either carrying one of the legacy display names or naming one of our ports.</summary>
    internal static bool IsConflict(string[] f, ISet<string> ownedKeys, ISet<int> ports, ISet<string> legacyNames, ISet<string>? programs = null)
    {
        // f: key|display|enabled|direction|action|profile|protocol|localPort|remote|program
        if (f.Length != 10 || ownedKeys.Contains(f[0])) return false;
        if (!string.Equals(f[2], "True", StringComparison.OrdinalIgnoreCase) || !string.Equals(f[3], "Inbound", StringComparison.OrdinalIgnoreCase) || !string.Equals(f[4], "Allow", StringComparison.OrdinalIgnoreCase)) return false;
        if (legacyNames.Contains(f[1])) return true;
        if (programs != null && programs.Any(x => string.Equals(x, f[9], StringComparison.OrdinalIgnoreCase))) return true;   // any-port allow for our exe or vlc.exe
        return f[7].Split(',').Any(p => int.TryParse(p, out var n) && ports.Contains(n));
    }

    /// <summary>Report, never touch: other enabled inbound allow rules that name our ports or carry known earlier Zephyr display names. (ok=false when the enumeration failed.)</summary>
    internal static (bool ok, List<string> conflicts) Conflicts(ISet<string> ownedKeys, ISet<int> ports, ISet<string> legacyNames, ISet<string>? programs = null)
    {
        var script =
            "$ErrorActionPreference='Stop'; try { $all=@(Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True) } catch { 'ERR'; exit 0 }; " +
            "foreach ($x in $all) { try { $p=$x|Get-NetFirewallPortFilter; $a=$x|Get-NetFirewallAddressFilter; $ap=$x|Get-NetFirewallApplicationFilter; " +
            "$x.Name+'|'+$x.DisplayName+'|'+$x.Enabled+'|'+$x.Direction+'|'+$x.Action+'|'+$x.Profile+'|'+$p.Protocol+'|'+($p.LocalPort -join ',')+'|'+($a.RemoteAddress -join ',')+'|'+$ap.Program } catch { } }";
        var (exit, output) = RunPs(script);
        if (exit != 0 || output.Trim() == "ERR") return (false, new List<string>());
        var list = new List<string>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.Trim().Split('|');
            if (IsConflict(f, ownedKeys, ports, legacyNames, programs)) list.Add($"'{f[1]}' profile {f[5]} {f[6]} port {f[7]} remote {f[8]} program {f[9]}");
        }
        return (true, list);
    }

    /// <summary>Removes only journaled rules, by their unique Name key, while the by-key read still equals the journal; then the key must read back Absent. Returns the entries that remain.</summary>
    internal static List<RuleEntry> RemoveJournaled(IEnumerable<RuleEntry> entries)
    {
        var keep = new List<RuleEntry>();
        foreach (var e in entries)
        {
            var now = ReadByKey(e.Key);
            if (now.Found == Found.Absent) continue;
            if (now.Found == Found.One && now.Display == e.DisplayName && now.Props == e.Props
                && RunPs("$ErrorActionPreference='Stop'; Remove-NetFirewallRule -Name '" + e.Key + "'").exit == 0
                && ReadByKey(e.Key).Found == Found.Absent) continue;
            LifeLog.Log("uninstall cleanup: firewall rule '" + e.DisplayName + "' (key " + e.Key + ") left (" + now.Found + ", not provably unchanged, or removal failed).");
            keep.Add(e);
        }
        return keep;
    }

    /// <summary>Uninstall cleanup entry: journaled rules (and, when the caller supplies it, the journaled watchdog) only. No journal, a corrupt one, or an untrusted directory leaves everything as found.</summary>
    internal static void UninstallCleanup(Func<WatchdogEntry, bool>? removeWatchdog, Func<UrlAclEntry, bool>? removeUrlAcl = null)
    {
        var (st, j) = LifecycleJournalStore.Read();
        if (st == JournalState.Absent) { LifeLog.Log("uninstall cleanup: no lifecycle journal; firewall rules and watchdog task left as found."); return; }
        if (st == JournalState.Corrupt || j == null) { LifeLog.Log("uninstall cleanup: lifecycle journal unreadable or untrusted; firewall rules and watchdog task left."); return; }
        var keepRules = RemoveJournaled(j.Rules);
        var keepWd = j.Watchdog != null && (removeWatchdog == null || !removeWatchdog(j.Watchdog)) ? j.Watchdog : null;
        var keepAcl = j.UrlAcl != null && (removeUrlAcl == null || !removeUrlAcl(j.UrlAcl)) ? j.UrlAcl : null;
        foreach (var u in j.Uncertain) LifeLog.Log("uninstall cleanup: possible orphan firewall rule '" + u.DisplayName + "' key " + u.Key + " (" + u.Outcome + "): never created-witnessed, not removed.");
        LifecycleJournalStore.Replace(new LifecycleJournal(1, keepRules, keepWd, j.Uncertain, keepAcl));
    }
}
