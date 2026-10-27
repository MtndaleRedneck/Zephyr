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
    /// </summary>
    internal static class FirewallSetup
    {
        public const string TcpRuleName = "Zephyr Player";
        public const string UdpRuleName = "Zephyr Player Discovery";

        public static bool RulesExist()
        {
            var tcp = Run("netsh", $"advfirewall firewall show rule name=\"{TcpRuleName}\"");
            if (tcp.ExitCode != 0 || !tcp.Output.Contains(TcpRuleName)) return false;
            var udp = Run("netsh", $"advfirewall firewall show rule name=\"{UdpRuleName}\"");
            return udp.ExitCode == 0 && udp.Output.Contains(UdpRuleName);
        }

        public static bool UrlAclExists()
        {
            var acl = Run("netsh", "http show urlacl");
            return acl.Output.Contains(":" + PlaybackServer.DefaultPort + "/");
        }

        /// <summary>Runs inside the elevated copy. [userSid] is the UNelevated user's
        /// SID, passed by the parent: the listen permission is scoped to that account,
        /// not everyone, even when another admin approved the prompt.</summary>
        public static int RunElevated(string? userSid)
        {
            try
            {
                var sid = userSid ?? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                var exe = Environment.ProcessPath ?? "";
                Run("netsh", $"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
                var tcp = Run("netsh", $"advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={PlaybackServer.DefaultPort} program=System profile=private remoteip=localsubnet");
                if (tcp.ExitCode != 0) return 2;
                Run("netsh", $"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
                var udp = Run("netsh", $"advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={DiscoveryResponder.OwnPort} program=\"{exe}\" profile=private remoteip=localsubnet");
                if (udp.ExitCode != 0) return 2;
                Run("netsh", "http delete urlacl url=http://+:" + PlaybackServer.DefaultPort + "/");
                var acl = Run("netsh", "http add urlacl url=http://+:" + PlaybackServer.DefaultPort + "/ sddl=D:(A;;GX;;;" + sid + ")");
                if (acl.ExitCode != 0 || !UrlAclExists()) return 3;
                // Verify the reservation really names the intended user, not just the port.
                if (sid != null && !UrlAclBlock().Contains(sid)) return 3;
                return RulesExist() ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Firewall setup failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>Remove what setup added (uninstall): both rules + the listen
        /// permission. Only the app's own named rules are touched.</summary>
        public static int RemoveElevated()
        {
            Run("netsh", $"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
            Run("netsh", $"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
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
                return false;
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
                if (p == null || !p.WaitForExit(60_000) || p.ExitCode != 0 || !RulesExist() || !UrlAclExists())
                    throw new InvalidOperationException("Setup could not be verified.");
                return true;
            }
            catch
            {
                Note("No firewall rule was added, so the phone cannot reach this PC yet. Zephyr Player will now close and offer to set up again next time you open it.");
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
                var b = new Button
                {
                    Content = label,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(16, 6, 16, 6),
                    Background = primary ? new SolidColorBrush(Brand.Orange) : card,
                    Foreground = primary ? bg : fg,
                    BorderThickness = new Thickness(0),
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
                dlg.Content = null;
            }
            var stack = new StackPanel();
            stack.Children.Add(body);
            stack.Children.Add(buttons);
            dlg.Content = stack;
            return dlg;
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
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
    }
}
