using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

        private static string MarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZephyrPlayer", "firewall.ok");

        /// <summary>True when setup verifiably matches the current shape: either a
        /// current-version marker (fast path - no netsh on every launch, round-7 N7-2)
        /// or the ACTUAL rules + ACL checked live, which also stamps the marker.</summary>
        public static bool SetupIsCurrent()
        {
            try
            {
                if (File.ReadAllText(MarkerPath).StartsWith(MarkerVersion + "\n")) return true;
            }
            catch { }
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
            var tcp = Run("netsh", $"advfirewall firewall show rule name=\"{TcpRuleName}\"");
            if (tcp.ExitCode != 0 || !tcp.Output.Contains(TcpRuleName)) return false;
            var udp = Run("netsh", $"advfirewall firewall show rule name=\"{UdpRuleName}\"");
            return udp.ExitCode == 0 && udp.Output.Contains(UdpRuleName);
        }

        /// <summary>Both rules exist AND carry the current shape (round-7 B7-1): TCP
        /// program=System + LocalSubnet + Private on 47823, UDP exe-scoped + LocalSubnet
        /// + Private on 8490. The 1.1.0 rules share the names but not the shape.</summary>
        public static bool RulesHaveCurrentShape()
        {
            return RuleBlockMatches(TcpRuleName, "TCP", PlaybackServer.DefaultPort, "System")
                && RuleBlockMatches(UdpRuleName, "UDP", DiscoveryResponder.OwnPort, Environment.ProcessPath ?? "");
        }

        private static bool RuleBlockMatches(string ruleName, string protocol, int port, string program)
        {
            var output = Run("netsh", $"advfirewall firewall show rule name=\"{ruleName}\" verbose").Output;
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

        public static bool UrlAclExists()
        {
            var acl = Run("netsh", "http show urlacl");
            return acl.Output.Contains(":" + PlaybackServer.DefaultPort + "/");
        }

        /// <summary>True when the port's reservation names the CURRENT user (a read-only
        /// check the unelevated app can run).</summary>
        public static bool UrlAclNamesCurrentUser()
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            return sid != null && UrlAclNamesUser(sid);
        }

        /// <summary>Who the port's URL reservation belongs to: us (an exact per-user
        /// grant for [sid]), a legacy broad grant the published 1.1.0 wrote (Everyone,
        /// nothing else), nobody, or something else - another app or another Windows
        /// user, which must never be overwritten. Parsed-descriptor comparisons on both
        /// sides: netsh stores well-known accounts as SDDL aliases (RID-500 admin comes
        /// back "LA", Everyone "WD"), so substring checks mislead.</summary>
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
                // Re-add our own rules from scratch: by name they are ours, whatever shape
                // the 1.1.0 installer gave them (any-address TCP 47823, UDP 8489 - which
                // was BingeMote's port and must go). Other apps' rules are never named.
                Run("netsh", $"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
                var tcp = Run("netsh", $"advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={PlaybackServer.DefaultPort} program=System profile=private remoteip=localsubnet");
                if (tcp.ExitCode != 0) return 2;
                Run("netsh", $"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
                var udp = Run("netsh", $"advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={DiscoveryResponder.OwnPort} program=\"{exe}\" profile=private remoteip=localsubnet");
                if (udp.ExitCode != 0) return 2;
                switch (ClassifyUrlAcl(sid))
                {
                    case AclOwnership.Ours:
                        break; // already exactly right
                    case AclOwnership.None:
                    case AclOwnership.Legacy:
                        Run("netsh", "http delete urlacl url=http://+:" + PlaybackServer.DefaultPort + "/");
                        var acl = Run("netsh", "http add urlacl url=http://+:" + PlaybackServer.DefaultPort + "/ sddl=D:(A;;GX;;;" + sid + ")");
                        if (acl.ExitCode != 0 || !UrlAclExists()) return 3;
                        break;
                    case AclOwnership.Foreign:
                        Console.Error.WriteLine("The network port Zephyr Player needs is reserved for another app or another Windows user on this PC.");
                        return 4;
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
                    var namesUs = (location.Length > 0 && string.Equals(location, appDir, StringComparison.OrdinalIgnoreCase))
                        || uninstall.IndexOf(appDir, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (version.StartsWith("1.1") && namesUs)
                    {
                        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                        Console.Error.WriteLine("Removed the leftover 1.1 uninstall entry (pointed at this install).");
                    }
                }
            }
            catch { }
        }

        /// <summary>Remove what setup added (uninstall): both rules and, when it is
        /// ours, the listen permission. [userSid] is the UNINSTALLING user's SID (the
        /// unelevated uninstaller passes it): the ACL is deleted only when it names
        /// that user or is the broad 1.1.0 Everyone grant - another user's or another
        /// app's reservation stays.</summary>
        public static int RemoveElevated(string? userSid)
        {
            Run("netsh", $"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
            Run("netsh", $"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
            string? sid = null;
            if (!string.IsNullOrWhiteSpace(userSid))
            {
                try { sid = new System.Security.Principal.SecurityIdentifier(userSid).Value; }
                catch { sid = null; }
            }
            var owner = ClassifyUrlAcl(sid);
            if (owner == AclOwnership.Ours || owner == AclOwnership.Legacy)
                Run("netsh", "http delete urlacl url=http://+:" + PlaybackServer.DefaultPort + "/");
            else if (owner == AclOwnership.Foreign)
                Console.Error.WriteLine("The listen permission on " + PlaybackServer.DefaultPort + " belongs to another app or Windows user; left in place.");
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
                if (p.ExitCode != 0 || !RulesHaveCurrentShape() || !UrlAclExists())
                    throw new InvalidOperationException("Setup could not be verified.");
                StampMarker();
                return true;
            }
            catch
            {
                if (conflict)
                    Note("Something else on this PC has already reserved the network port Zephyr Player needs (" + PlaybackServer.DefaultPort + "), and it is not one of ours, so setup stopped without changing it. Zephyr Player will close now; freeing that port needs whoever manages this PC.");
                else
                    // True in every failure case (declined UAC, partial setup, timeout):
                    // setup did not finish - rules may or may not exist.
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

        private static void Note(string text)
        {
            var dlg = DarkDialog(text, "OK", null, () => { });
            dlg.ShowDialog();
        }

        private static Window DarkDialog(string text, string confirmLabel, string? cancelLabel, Action onConfirm)
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1E));
            var card = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2C));
            var fg = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xF0));
            var dlg = new Window
            {
                Title = "Zephyr Player",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = bg,
                Foreground = fg,
                Topmost = true,
            };
            WindowChrome.ApplyDarkTitleBar(dlg);
            var body = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 20, 20, 8),
                FontSize = 13,
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 8, 20, 20),
            };
            Button MakeBtn(string label, bool primary)
            {
                var back = primary ? new SolidColorBrush(Brand.Orange) : card;
                var hover = primary
                    ? new SolidColorBrush(Color.FromRgb(0xCC, 0x77, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3B));
                var b = new Button
                {
                    Content = label,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(16, 6, 16, 6),
                    Background = back,
                    Foreground = primary ? bg : fg,
                    BorderThickness = new Thickness(0),
                    // A real template: the stock one paints the light-blue classic hover
                    // chrome and the dotted focus box over the dark look (round-7 S7-2).
                    Template = DarkButtonTemplate(hover),
                    FocusVisualStyle = null,
                };
                return b;
            }
            var ok = MakeBtn(confirmLabel, true);
            ok.Click += (_, _) => { onConfirm(); dlg.DialogResult = true; dlg.Close(); };
            buttons.Children.Add(ok);
            if (cancelLabel != null)
            {
                var cancel = MakeBtn(cancelLabel, false);
                cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
                buttons.Children.Add(cancel);
            }
            var stack = new StackPanel();
            stack.Children.Add(body);
            stack.Children.Add(buttons);
            dlg.Content = stack;
            return dlg;
        }

        /// <summary>A minimal dark button chrome: rounded fill bound to the button's own
        /// Background (slightly lighter on hover), centered text, no light system chrome.</summary>
        private static ControlTemplate DarkButtonTemplate(Brush hoverBackground)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            text.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(text);
            template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, hoverBackground));
            template.Triggers.Add(hover);
            return template;
        }

        private static bool UrlAclNamesUser(string sid)
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
            var all = Run("netsh", "http show urlacl").Output;
            foreach (var block in all.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None))
                if (block.Contains(":" + PlaybackServer.DefaultPort + "/")) return block;
            return "";
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
