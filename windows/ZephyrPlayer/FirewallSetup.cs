using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ZephyrPlayer
{
    /// <summary>
    /// The one-time firewall + listen-permission setup, owned by the app's EXPLAINED
    /// first-run elevation (round-6 N-8): the installer is per-user and never touches
    /// the firewall, so nothing lands in the wrong profile when someone else's admin
    /// credentials would have answered an install-time prompt.
    ///
    /// Rule shape (Microsoft http.sys grounding): the control server is an
    /// HttpListener, so inbound TCP is attributed to the System binary in the kernel -
    /// an exe-scoped TCP rule would never match. TCP 47823 is therefore
    /// program=System + LocalSubnet + Private, and the per-user URL ACL is the real
    /// limit on who may take the port. UDP discovery lives in THIS process and keeps an
    /// exe-scoped rule. Both rules are Private-networks-only.
    ///
    /// Upgrades (round-7 B7-1): a name-only check passes on the 1.1.0 shape, so the
    /// marker is VERSIONED and verification reads the ACTUAL rule shape and ACL owner.
    /// Anything old triggers one explained elevation that migrates rules + ACL.
    /// </summary>
    internal static class FirewallSetup
    {
        public const string TcpRuleName = "Zephyr Player";
        public const string UdpRuleName = "Zephyr Player Discovery";

        /// <summary>First line of the firewall marker. Bump when the required shape changes.</summary>
        internal const string MarkerVersion = "v2";

        /// <summary>Who the port's URL reservation belongs to.</summary>
        internal enum AclOwnership { None, Ours, Legacy, Foreign }

        // The marker is per user ON THIS MACHINE (Local, not Roaming): a Roaming marker
        // would follow the user to another PC and vouch for rules that do not exist
        // there (round-8).
        private static string MarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZephyrPlayer", "firewall.ok");

        /// <summary>True only when the ACTUAL rules + ACL check live, right now. There is
        /// no marker fast path: a rule deleted or changed after the stamp - or a marker
        /// left behind by an older install - must never vouch for the shape (round-8).
        /// The COM read is cheap and needs no admin. The marker is still stamped (the
        /// upgrade smoke asserts it) but nothing trusts it.</summary>
        public static bool SetupIsCurrent()
        {
            LastReadFailed = false;
            if (!RulesHaveCurrentShape() || !UrlAclNamesCurrentUser()) return false;
            StampMarker();
            return true;
        }

        private static void StampMarker()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, MarkerVersion + "\n" + DateTime.UtcNow.ToString("O"));
            }
            catch { }
        }

        public static bool RulesExist()
        {
            var rules = ReadRulesByName(TcpRuleName, UdpRuleName);
            return rules != null
                && rules.Any(r => string.Equals(r.Name, TcpRuleName, StringComparison.OrdinalIgnoreCase))
                && rules.Any(r => string.Equals(r.Name, UdpRuleName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>One firewall rule's shape, read through the Windows Firewall COM API
        /// (HNetCfg.FwPolicy2). Rule DATA is language-independent - unlike netsh's
        /// display text, which is localized and made the old text parse re-ask for setup
        /// on every launch on a non-English PC (round-8 N-1).</summary>
        internal sealed record RuleShape(string Name, int Protocol, string LocalPorts, int Direction,
            bool Enabled, int Profiles, string RemoteAddresses, string ApplicationName, int Action);

        /// <summary>True when the last firewall/ACL READ failed at the API level (as
        /// opposed to "read fine, not ours"). A read failure is a distinct state: the
        /// user is told Windows Firewall settings could not be read, and setup does NOT
        /// re-elevate, because admin cannot fix an unreadable API (round-9 N9-8).</summary>
        internal static bool LastReadFailed;

        /// <summary>All rules carrying any of [names], via the firewall COM API. Null
        /// when the API itself failed (treated as "not current" - setup is re-offered).</summary>
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
                System.Diagnostics.Debug.WriteLine("Firewall rules could not be read: " + ex.Message);
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

        /// <summary>Both rules exist AND carry the current shape (round-7 B7-1): TCP
        /// program=System + LocalSubnet + Private on 47823, UDP exe-scoped + LocalSubnet
        /// + Private on 8490. EVERY rule under each name must match (round-8 N-3: a
        /// stale broader rule beside a correct one still fails). The 1.1.0 rules share
        /// the names but not the shape.</summary>
        public static bool RulesHaveCurrentShape()
        {
            return RulesHaveCurrentShape(ReadRulesByName(TcpRuleName, UdpRuleName), Environment.ProcessPath ?? "");
        }

        /// <summary>CI hook entry: check the rules against an EXPLICIT expected exe
        /// path. The hooked CI exe runs from a capture folder, so its own ProcessPath
        /// is never the path the installed app's UDP rule names (round-10 gate
        /// 40508776538: the unelevated check failed on its own path, not the shape).</summary>
        internal static bool RulesHaveCurrentShapeFor(string exePath)
        {
            return RulesHaveCurrentShape(ReadRulesByName(TcpRuleName, UdpRuleName), exePath);
        }

        internal static bool RulesHaveCurrentShape(List<RuleShape>? rules, string exePath)
        {
            if (rules == null) return false;
            var tcp = rules.Where(r => string.Equals(r.Name, TcpRuleName, StringComparison.OrdinalIgnoreCase)).ToList();
            var udp = rules.Where(r => string.Equals(r.Name, UdpRuleName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tcp.Count == 0 || udp.Count == 0) return false;
            return tcp.All(r => RuleMatches(r, 6, PlaybackServer.DefaultPort, "System"))
                && udp.All(r => RuleMatches(r, 17, DiscoveryResponder.OwnPort, exePath));
        }

        public static bool UrlAclExists()
        {
            return QueryUrlAcl("http://+:" + PlaybackServer.DefaultPort + "/").Found;
        }

        /// <summary>True when the port's reservation names the CURRENT user (a read-only
        /// check the unelevated app can run).</summary>
        public static bool UrlAclNamesCurrentUser()
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            return sid != null && UrlAclNamesUser(sid);
        }

        /// <summary>The port's reservation record straight from http.sys (typed API,
        /// no netsh display text - the "SDDL:" label is localized and a regex on it
        /// fails OPEN; round-9 N9-3). Found=false: no reservation for the exact URL.
        /// Sddl null on any read error: treated as existing-but-unreadable, i.e.
        /// Foreign - we never delete what we cannot read.</summary>
        private static (bool Found, string? Sddl) QueryUrlAcl(string urlPrefix)
        {
            // http.h lifecycle: HttpInitialize with HTTP_INITIALIZE_CONFIG is required
            // before the query, HttpTerminate after.
            uint initRc = HttpInitialize(new HttpApiVersion { Major = 1, Minor = 0 }, HttpInitializeConfig, IntPtr.Zero);
            if (initRc != 0)
            {
                LastReadFailed = true;
                Console.Error.WriteLine("http.sys init failed, error " + initRc + " - treating the reservation as unreadable/foreign.");
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
                // The record's strings live inside the output buffer past the struct,
                // so size for content: on ERROR_INSUFFICIENT_BUFFER/ERROR_MORE_DATA the
                // API reports the needed size through pReturnLength (http.h). Any other
                // error is a READ error - never "no reservation" (round-9 N9-3
                // fail-closed, round-10 B10-1).
                var outSize = 256;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(outSize);
                    try
                    {
                        uint rc = HttpQueryServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclQueryId,
                            ref query, System.Runtime.InteropServices.Marshal.SizeOf(query), buf, outSize,
                            out int needed, IntPtr.Zero);
                        // No matching record: ERROR_FILE_NOT_FOUND (2) per http.h
                        // (round-10 B10-1). NO other code maps to absent: a made-up
                        // "not found" would route an EXISTING reservation through the
                        // None branch and its blind delete. Every other error is a
                        // read error, i.e. Foreign. The live tests pin what absent
                        // returns.
                        if (rc == ErrorInsufficientBuffer || rc == ErrorMoreData)
                        {
                            if (needed > outSize) { outSize = needed; continue; }
                            LastReadFailed = true;
                            return (true, null);
                        }
                        // No matching record: ERROR_FILE_NOT_FOUND (2) ONLY. Gate
                        // 40511800617 proved the runner returns 2 for an absent
                        // record (the live test logged "error 2" on the absent
                        // plant). Every other nonzero code is a READ error, i.e.
                        // foreign - never "no reservation" (round-9 N9-3, B10-1).
                        if (rc == ErrorFileNotFound) return (false, null);
                        if (rc != 0)
                        {
                            LastReadFailed = true;
                            Console.Error.WriteLine("http.sys URL ACL query failed, error " + rc + " - treating the reservation as unreadable/foreign.");
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
        private const uint ErrorInsufficientBuffer = 122;
        private const uint ErrorMoreData = 234;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct HttpServiceConfigUrlAclKey
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            public string UrlPrefix;
        }

        // http.h: { QueryDesc, KeyDesc, dwToken } - QueryDesc FIRST (round-10 B10-1).
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

        /// <summary>Pure classification of one reservation SDDL: us (an exact per-user
        /// grant for [sid]), a legacy broad grant the published 1.1.0 wrote (Everyone,
        /// nothing else), nobody, or something else - another app or another Windows
        /// user, which must never be overwritten. Parsed-descriptor comparisons on both
        /// sides (netsh/http.sys store well-known accounts as SDDL aliases, so substring
        /// checks mislead), and EXACT whole-descriptor equality: a multi-ACE descriptor
        /// that merely CONTAINS our user or an Everyone ACE is foreign (round-9 T4).</summary>
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

        /// <summary>Classifies the port's LIVE reservation. A reservation that exists
        /// but cannot be read or parsed is Foreign, never None (round-9 N9-3: the old
        /// netsh-text version returned None on an unparsed block and setup would have
        /// DELETED a reservation it could not read).</summary>
        internal static AclOwnership ClassifyUrlAcl(string? sid)
        {
            var (found, sddl) = QueryUrlAcl("http://+:" + PlaybackServer.DefaultPort + "/");
            if (!found) return AclOwnership.None;
            return ClassifySddl(sddl, sid);
        }

        /// <summary>Runs inside the elevated copy. [userSid] is the UNelevated user's
        /// SID, passed by the parent: the listen permission is scoped to that account,
        /// not everyone, even when another admin approved the prompt. Exit 4 means the
        /// port is reserved by another app or another Windows user; their reservation
        /// is NEVER overwritten - the caller explains the conflict instead.</summary>
        public static int RunElevated(string? userSid)
        {
            try
            {
                // Validate the passed SID before it goes anywhere near an SDDL string.
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
                var exe = Environment.ProcessPath ?? "";
                // Round-10 N10-4: clear the inherited 1.1.0 per-machine uninstall entry
                // on every elevated run, success or failure (exact 1.1.x + folder match
                // only). While it exists, Apps & Features can fire 1.1.0's blind delete.
                try { RemoveLegacy110UninstallEntry(); } catch { }
                // Classify the port reservation FIRST (round-9 N9-5): when it belongs to
                // another app or another Windows user, setup stops BEFORE touching any
                // machine-wide rule - rewriting the shared rules first would break the
                // other install even though setup then stopped with the conflict exit.
                var aclOwner = ClassifyUrlAcl(sid);
                if (aclOwner == AclOwnership.Foreign)
                {
                    Console.Error.WriteLine("The network port Zephyr Player needs is reserved for another app or another Windows user on this PC.");
                    return 4;
                }
                // Re-add our own rules from scratch: by name they are ours, whatever shape
                // the 1.1.0 installer gave them (any-address TCP 47823, UDP 8489 - which
                // was BingeMote's port and must go). Other apps' rules are never named.
                Run("netsh", $"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
                var tcp = Run("netsh", $"advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={PlaybackServer.DefaultPort} program=System profile=private remoteip=localsubnet");
                if (tcp.ExitCode != 0) return 2;
                Run("netsh", $"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
                var udp = Run("netsh", $"advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={DiscoveryResponder.OwnPort} program=\"{exe}\" profile=private remoteip=localsubnet");
                if (udp.ExitCode != 0) return 2;
                switch (aclOwner)
                {
                    case AclOwnership.Ours:
                        break; // already exactly right
                    case AclOwnership.None:
                    case AclOwnership.Legacy:
                        Run("netsh", "http delete urlacl url=http://+:" + PlaybackServer.DefaultPort + "/");
                        var acl = Run("netsh", "http add urlacl url=http://+:" + PlaybackServer.DefaultPort + "/ sddl=D:(A;;GX;;;" + sid + ")");
                        if (acl.ExitCode != 0 || !UrlAclExists()) return 3;
                        break;
                }
                // Verify the reservation really names the intended user, not just the port.
                if (!UrlAclNamesUser(sid)) return 3;
                RemoveLegacy110UninstallEntry();
                return RulesExist() ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Firewall setup failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>The published 1.1.0 admin install leaves an HKLM uninstall entry
        /// behind next to the new per-user one; running it would fire 1.1.0's own
        /// uninstall steps. Remove it only when it verifiably names THIS install folder
        /// and the published 1.1.x version - anything else stays untouched.</summary>
        private static void RemoveLegacy110UninstallEntry()
        {
            try
            {
                var appDir = (Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "").TrimEnd('\\');
                if (appDir.Length == 0) return;
                foreach (var path in new[]
                {
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Zephyr Player_is1",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Zephyr Player_is1",
                })
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: true);
                    if (key == null) continue;
                    var version = key.GetValue("DisplayVersion") as string ?? "";
                    var location = (key.GetValue("InstallLocation") as string ?? "").Trim().Trim('"').TrimEnd('\\');
                    var uninstall = key.GetValue("UninstallString") as string ?? "";
                    // Exact folder, never a prefix ("Zephyr Player 2" must not match), and
                    // a SEMANTIC 1.1.x version ("1.10" is not 1.1) - round-8 N-12.
                    var namesUs = (location.Length > 0 && string.Equals(location, appDir, StringComparison.OrdinalIgnoreCase))
                        || SameFolder(UninstallDir(uninstall), appDir);
                    if (IsVersion11(version) && namesUs)
                    {
                        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                        Console.Error.WriteLine("Removed the leftover 1.1 uninstall entry (pointed at this install).");
                    }
                }
            }
            catch { }
        }

        private static bool IsVersion11(string v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^(\d+)\.(\d+)(\.\d+)*$");
            return m.Success && m.Groups[1].Value == "1" && m.Groups[2].Value == "1";
        }

        private static string UninstallDir(string uninstallString)
        {
            var u = uninstallString.Trim().Trim('"');
            try { return (Path.GetDirectoryName(u) ?? "").TrimEnd('\\'); } catch { return ""; }
        }

        private static bool SameFolder(string a, string b) =>
            a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Remove what setup added (uninstall): both rules and, when it is
        /// ours, the listen permission. [userSid] is the UNINSTALLING user's SID (the
        /// unelevated uninstaller passes it): the ACL is deleted only when it names
        /// that user or is the broad 1.1.0 Everyone grant - another user's or another
        /// app's reservation stays.</summary>
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
                // whoami gave the uninstaller nothing: without the user's SID our
                // per-user grant cannot be told from another user's, so the reservation
                // STAYS (round-8 N-11: never misclassify an empty SID as a foreign owner).
                Console.Error.WriteLine("The uninstalling user's account could not be determined, so the listen permission on " + PlaybackServer.DefaultPort + " was left in place.");
                return 0;
            }
            // Classify the reservation BEFORE touching any rule (round-9 N9-5): when it
            // is another app's or another Windows user's, their install likely owns the
            // machine-wide rules too - deleting the rules out from under them would
            // break their Player, so nothing is touched.
            var owner = ClassifyUrlAcl(sid);
            if (owner == AclOwnership.Foreign)
            {
                Console.Error.WriteLine("The listen permission on " + PlaybackServer.DefaultPort + " belongs to another app or Windows user; the firewall rules were left in place too.");
                return 0;
            }
            Run("netsh", $"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
            Run("netsh", $"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
            if (owner == AclOwnership.Ours || owner == AclOwnership.Legacy)
                Run("netsh", "http delete urlacl url=http://+:" + PlaybackServer.DefaultPort + "/");
            return 0;
        }

        /// <summary>Explains the prompt (dark dialog), relaunches elevated, and returns
        /// true only when the rules + listen permission are really in place. A decline
        /// or failure shows ONE dark note and returns false so the app closes cleanly
        /// instead of failing its listen with a misleading error.</summary>
        public static bool ExplainAndRelaunch()
        {
            if (!Explain(
                "One Windows permission, then setup is done. " +
                "Zephyr Player needs to let your phone reach this PC on your home network, so Windows will now ask for administrator permission once. " +
                "It adds two firewall rules for home (Private) networks only - one for the phone connection and one for phone discovery - plus the listen permission for the phone connection.",
                "Continue"))
            {
                Note("No problem - setup will be offered again next time you open Zephyr Player.");
                return false;
            }
            var conflict = false;
            try
            {
                var exe = Environment.ProcessPath!;
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
                var psi = new ProcessStartInfo(exe, "--install-firewall \"" + sid + "\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                var p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("The elevated setup did not start.");
                if (!p.WaitForExit(60_000)) throw new InvalidOperationException("Setup timed out.");
                conflict = p.ExitCode == 4;
                LastReadFailed = false;
                var verified = RulesHaveCurrentShape() && UrlAclExists();
                if (p.ExitCode != 0 || !verified)
                {
                    if (LastReadFailed)
                        // Elevation cannot fix an unreadable firewall/ACL API: say so,
                        // and do NOT re-elevate or retry in a loop (round-9 N9-8).
                        Note("Windows Firewall settings could not be read on this PC, so setup cannot check its own work. Zephyr Player will close now; a restart of Windows usually fixes the read.");
                    throw new InvalidOperationException("Setup could not be verified.");
                }
                StampMarker();
                return true;
            }
            catch
            {
                if (conflict)
                    Note("Something else on this PC has already reserved the network port Zephyr Player needs (" + PlaybackServer.DefaultPort + "), and it is not one of ours, so setup stopped without changing it. Zephyr Player will close now; freeing that port needs whoever manages this PC.");
                else if (!LastReadFailed)
                    // True in every failure case (declined UAC, partial setup, timeout):
                    // setup did not finish - rules may or may not exist. A read failure
                    // already said its own plainer note above (round-9 N9-8).
                    Note("Setup did not finish, so the phone cannot reach this PC yet. Zephyr Player will now close and offer to set up again next time you open it.");
                return false;
            }
        }

        /// <summary>A small dark explanation with a labeled confirm (the settled look;
        /// MessageBox would come up light).</summary>
        private static bool Explain(string text, string confirmLabel)
        {
            var answer = false;
            var dlg = DarkDialog(text, confirmLabel, "Cancel", () => answer = true);
            dlg.ShowDialog();
            return answer;
        }

        /// <summary>The same dark note (single OK, Enter/Esc close) with its own title,
        /// built but not shown: the player's subtitle-size restart popup.</summary>
        internal static Window NoteWindow(string text, string title)
        {
            var dlg = DarkDialog(text, "OK", null, () => { });
            dlg.Title = title;
            return dlg;
        }

        internal static void Note(string text)
        {
            var dlg = DarkDialog(text, "OK", null, () => { });
            dlg.ShowDialog();
        }

        private static Window DarkDialog(string text, string confirmLabel, string? cancelLabel, Action onConfirm)
        {
            // CP4 item 84: the 2.0 theme (Settings surface #0E0E11, #F5F5F7 text, the .btn /
            // .btn.or buttons with the shared hover/pressed layers). Behaviour is unchanged.
            var bg = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x11)));
            var fg = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
            var body = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEB)));
            var dlg = new Window
            {
                Title = "Zephyr Player",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = bg,
                Foreground = fg,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Topmost = true,
            };
            WindowChrome.ApplyDarkTitleBar(dlg);
            var message = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 20, 22, 8),
                FontSize = 13,
                LineHeight = 19,
                Foreground = body,
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 10, 20, 20),
            };
            Button MakeBtn(string label, bool primary)
            {
                var b = new Button
                {
                    Content = label,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(16, 6, 16, 6),
                    MinHeight = 32,
                    FontSize = 13,
                    FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
                    Background = primary ? AccentGradient : Frozen(new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF))),
                    Foreground = primary ? Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16))) : Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEB))),
                    BorderThickness = new Thickness(1),
                    BorderBrush = primary ? Brushes.Transparent : Frozen(new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF))),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    // A real template: the stock one paints the light-blue classic hover
                    // chrome and the dotted focus box over the dark look (round-7 S7-2).
                    // Keyboard focus gets an OUTER 2 px white ring with a gap (round-11:
                    // 17:1 against the dialog in every state); hover and press lay the
                    // shared white layers over the fill (Hover #FFFFFF12, Pressed #FFFFFF1F,
                    // 120 ms ease, item 86), so the label keeps its contrast in every state.
                    Template = DarkButtonTemplate(),
                    FocusVisualStyle = null,
                };
                return b;
            }
            var ok = MakeBtn(confirmLabel, true);
            ok.IsDefault = true;   // Enter confirms
            ok.IsCancel = cancelLabel == null;   // a note's single OK also takes Esc
            ok.Click += (_, _) => { onConfirm(); dlg.DialogResult = true; dlg.Close(); };
            buttons.Children.Add(ok);
            if (cancelLabel != null)
            {
                var cancel = MakeBtn(cancelLabel, false);
                cancel.IsCancel = true;   // Esc cancels
                cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
                buttons.Children.Add(cancel);
            }
            var stack = new StackPanel();
            stack.Children.Add(message);
            stack.Children.Add(buttons);
            dlg.Content = new Border { BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))), BorderThickness = new Thickness(0, 1, 0, 0), Child = stack };
            return dlg;
        }

        /// <summary>A themed yes/no (the pairing reset): true only on the confirm button.
        /// Cancel is the safe default for Esc; owned by [owner] when it is shown.</summary>
        internal static bool Confirm(Window? owner, string text, string title, string confirmLabel, string cancelLabel)
        {
            var answer = false;
            var dlg = DarkDialog(text, confirmLabel, cancelLabel, () => answer = true);
            dlg.Title = title;
            if (owner != null && owner.IsVisible) { dlg.Owner = owner; dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            dlg.ShowDialog();
            return answer;
        }

        private static readonly LinearGradientBrush AccentGradient = MakeAccent();
        private static LinearGradientBrush MakeAccent()
        {
            var g = new LinearGradientBrush(Color.FromRgb(0xFF, 0xA2, 0x33), Color.FromRgb(0xFF, 0x95, 0x00), 90);
            g.Freeze();
            return g;
        }
        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

#if PAIR_CODE_HOOK
        // CI ONLY: render the permission dialogs with REAL interaction state and save
        // actual pixels (round-9 missing-captures follow-up). Hover/pressed come from
        // the real cursor parked over the shown dialog's primary button, focus from
        // real keyboard focus - the template triggers fire for real, then the live
        // visual tree is rendered. Compiled out of shipped builds like the other hooks.
        internal static void RunDialogShot(string mode, string path)
        {
            // Round-10 follow-up: the b452bbb gate ran this hook to exit 0 with no PNG
            // because the capture's catch swallowed the failure. Every step now logs to
            // stderr (lands in the job log) and a diag file next to the PNG, and any
            // failure exits nonzero - a failed leg must fail the step.
            var diagPath = System.IO.Path.ChangeExtension(path, ".diag.txt");
            try { File.Delete(diagPath); } catch { }
            void Log(string line)
            {
                Console.Error.WriteLine("[dialog-shot] " + line);
                try { File.AppendAllText(diagPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n"); } catch { }
            }
            var exitCode = 2;
            try
            {
                Log($"start mode={mode} path={path}");
                var note = mode == "note";
                var dlg = DarkDialog(
                    note
                        ? "Setup did not finish, so the phone cannot reach this PC yet. Zephyr Player will now close and offer to set up again next time you open it."
                        : "One Windows permission, then setup is done. Zephyr Player needs to let your phone reach this PC on your home network, so Windows will now ask for administrator permission once. It adds two firewall rules for home (Private) networks only - one for the phone connection and one for phone discovery - plus the listen permission for the phone connection.",
                    note ? "OK" : "Continue",
                    note ? null : "Cancel",
                    () => { });
                dlg.Show();
                dlg.Activate();
                Log("dialog shown");
                var ok = FirstButton(dlg);
                Log(ok == null ? "primary button NOT found" : "primary button found");
                var step = 0;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        step++;
                        Log("tick " + step);
                        if (step == 1)
                        {
                            if (mode == "focus" && ok != null)
                            {
                                // The cursor stays where the PREVIOUS shot run parked it
                                // (the desktop session outlives each shot process); over the
                                // button it fires IsMouseOver and the "focus" capture is
                                // really hover+focus (round-11). Park it in a screen corner.
                                SetCursorPos(8, 8);
                                dlg.Activate(); ok.Focus(); Log("focus set, cursor moved away");
                            }
                            else if ((mode == "hover" || mode == "pressed") && ok != null)
                            {
                                var pt = ok.PointToScreen(new Point(ok.ActualWidth / 2, ok.ActualHeight / 2));
                                SetCursorPos((int)Math.Round(pt.X), (int)Math.Round(pt.Y));
                                if (mode == "pressed") mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // LEFTDOWN, held
                                Log("cursor parked over primary");
                            }
                        }
                        else if (step >= 4)
                        {
                            timer.Stop();
                            try
                            {
                                var dpi = VisualTreeHelper.GetDpi(dlg);
                                var rtb = new RenderTargetBitmap(
                                    Math.Max(1, (int)Math.Ceiling(dlg.ActualWidth * dpi.DpiScaleX)),
                                    Math.Max(1, (int)Math.Ceiling(dlg.ActualHeight * dpi.DpiScaleY)),
                                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                                rtb.Render(dlg);
                                var enc = new PngBitmapEncoder();
                                enc.Frames.Add(BitmapFrame.Create(rtb));
                                using var fs = File.Create(path);
                                enc.Save(fs);
                                exitCode = 0;
                                Log("saved " + new FileInfo(path).Length + " bytes");
                                // Real-window capture WITH the title bar: RenderTargetBitmap
                                // draws only the client visual tree, so the shots above never
                                // show the dialog's title bar (round-11). Capture the on-screen
                                // window rectangle - what the user actually sees, chrome included.
                                var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                                RECT rc; GetWindowRect(hwnd, out rc);
                                var wpx = rc.Right - rc.Left; var hpx = rc.Bottom - rc.Top;
                                if (wpx <= 0 || hpx <= 0) throw new InvalidOperationException("window rect is empty");
                                var screenDc = GetDC(IntPtr.Zero);
                                var memDc = CreateCompatibleDC(screenDc);
                                var hBmp = CreateCompatibleBitmap(screenDc, wpx, hpx);
                                var oldBmp = SelectObject(memDc, hBmp);
                                BitBlt(memDc, 0, 0, wpx, hpx, screenDc, rc.Left, rc.Top, 0x00CC0020); // SRCCOPY
                                SelectObject(memDc, oldBmp);
                                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                var windowPath = System.IO.Path.Combine(
                                    System.IO.Path.GetDirectoryName(path)!,
                                    System.IO.Path.GetFileNameWithoutExtension(path) + "-window.png");
                                var enc2 = new PngBitmapEncoder();
                                enc2.Frames.Add(BitmapFrame.Create(src));
                                using (var fs2 = File.Create(windowPath)) enc2.Save(fs2);
                                DeleteObject(hBmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
                                Log("window capture saved " + new FileInfo(windowPath).Length + " bytes");
                            }
                            catch (Exception capEx)
                            {
                                Log("capture FAILED: " + capEx);
                            }
                            if (mode == "pressed") mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // LEFTUP
                            try { dlg.Close(); } catch { }
                            Application.Current?.Shutdown(exitCode);
                        }
                    }
                    catch (Exception tickEx)
                    {
                        Log("tick FAILED: " + tickEx);
                        try { timer.Stop(); } catch { }
                        Application.Current?.Shutdown(2);
                    }
                };
                timer.Start();
                // Watchdog: if the dispatcher never runs the ticks, fail loudly instead
                // of hanging the step.
                var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                watchdog.Tick += (_, _) =>
                {
                    watchdog.Stop();
                    Log("watchdog: capture never completed");
                    Application.Current?.Shutdown(3);
                };
                watchdog.Start();
            }
            catch (Exception ex)
            {
                Log("fatal: " + ex);
                Application.Current?.Shutdown(2);
            }
        }

        private static Button? FirstButton(DependencyObject root)
        {
            var n = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is Button b) return b;
                var r = FirstButton(c);
                if (r != null) return r;
            }
            return null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
#endif

        /// <summary>A minimal dark button chrome: rounded fill bound to the button's own
        /// Background, the shared hover/pressed white layers, centered text, an outer
        /// ring when keyboard-focused, no light system chrome.</summary>
        private static ControlTemplate DarkButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            // Round-11: the focus ring wraps the chrome - 2px with a 2px gap, painting
            // against the dark dialog background instead of the button fill.
            var ring = new FrameworkElementFactory(typeof(Border));
            ring.Name = "Ring";
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            ring.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            ring.SetValue(Border.PaddingProperty, new Thickness(2));
            var grid = new FrameworkElementFactory(typeof(Grid));
            var border = new FrameworkElementFactory(typeof(Border));
            // Round-9 N9-7: the template parts are NAMED and state changes target them;
            // setters on the button itself lose to its local values.
            border.Name = "Chrome";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.Name = "Label";
            text.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            text.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(text);
            grid.AppendChild(border);
            FrameworkElementFactory Layer(string name)
            {
                var l = new FrameworkElementFactory(typeof(Border));
                l.Name = name;
                l.SetValue(Border.BackgroundProperty, Brushes.White);
                l.SetValue(UIElement.OpacityProperty, 0.0);
                l.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                l.SetValue(UIElement.IsHitTestVisibleProperty, false);
                return l;
            }
            grid.AppendChild(Layer("Hl"));
            grid.AppendChild(Layer("Pr"));
            ring.AppendChild(grid);
            template.VisualTree = ring;
            // Hover #FFFFFF12 (7.06 %), pressed #FFFFFF1F (a 5.48 % layer on top), 120 ms ease.
            template.Triggers.Add(FadeTrigger(UIElement.IsMouseOverProperty, "Hl", 0.0706));
            template.Triggers.Add(FadeTrigger(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, "Pr", 0.0548));
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Ring"));
            template.Triggers.Add(focus);
            return template;
        }

        private static Trigger FadeTrigger(DependencyProperty property, string layer, double to)
        {
            Storyboard Fade(double value)
            {
                var a = new DoubleAnimation(value, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTargetName(a, layer);
                Storyboard.SetTargetProperty(a, new PropertyPath(UIElement.OpacityProperty));
                var s = new Storyboard();
                s.Children.Add(a);
                return s;
            }
            var t = new Trigger { Property = property, Value = true };
            t.EnterActions.Add(new BeginStoryboard { Storyboard = Fade(to) });
            t.ExitActions.Add(new BeginStoryboard { Storyboard = Fade(0) });
            return t;
        }

        private static bool UrlAclNamesUser(string sid)
        {
            // Exact whole-descriptor equality via the typed query; a SID substring is
            // never enough (round-9 T4: ...-1001 vs ...-10012).
            return ClassifyUrlAcl(sid) == AclOwnership.Ours;
        }

        private static (int ExitCode, string Output) Run(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            // Read both pipes concurrently: a full stderr would otherwise deadlock the
            // sequential stdout-then-stderr reads (round-7 N7-5).
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
    }
}
