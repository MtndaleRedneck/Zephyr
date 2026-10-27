using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ZephyrPlayer
{
    /// <summary>The network profile of the adapter the phone's traffic actually arrives on.</summary>
    internal sealed class NetProfile
    {
        public string Name = "";
        public string Category = "";
        public int InterfaceIndex;
    }

    /// <summary>
    /// Public-Wi-Fi detection, ported from the BingeMote helper's NetworkCheck.
    /// The installer's firewall rules are Private-networks-only (the right
    /// posture); when Windows thinks the Wi-Fi is "Public", the phone is
    /// silently blocked. Zephyr Player surfaces that as a NOTICE ONLY (owner's
    /// settled decision: no button that changes Windows settings).
    /// </summary>
    internal static class NetworkCheck
    {
        /// <summary>
        /// The profile of the adapter that owns the default IPv4 route, so a VPN
        /// or second adapter doesn't trip the warning. Null when it can't be
        /// determined. Never throws.
        /// </summary>
        public static NetProfile? DefaultRouteProfile()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell",
                    "-NoProfile -NonInteractive -Command \"$i=(Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceIndex; $p=Get-NetConnectionProfile -InterfaceIndex $i -ErrorAction SilentlyContinue; if ($p) { '{0}|{1}|{2}' -f $p.Name,$p.NetworkCategory,$p.InterfaceIndex }\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                // Wait first, read after: reading before the timeout would hang
                // forever on a stuck PowerShell.
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    return null;
                }
                var output = p.StandardOutput.ReadToEnd().Trim();
                var outputLine = output;
                return ParseProfileLine(outputLine);
            }
            catch { return null; }
        }

        /// <summary>Probe the active network; when it is Public, hand it to [show].
        /// Runs on a background thread; [show] marshals. Never throws.</summary>
        public static void CheckNow(Action<NetProfile> show)
        {
            Task.Run(() =>
            {
                NetProfile? p = null;
                try { p = DefaultRouteProfile(); } catch { }
                if (p == null || !p.Category.Equals("Public", StringComparison.OrdinalIgnoreCase)) return;
                try { show(p); } catch { }
            });
        }

        private static readonly object Gate = new();
        private static bool _queued;

        /// <summary>Network changes arrive in bursts; probe once, three seconds after the first of a burst.</summary>
        public static void QueueCheck(Action<NetProfile> show)
        {
            lock (Gate)
            {
                if (_queued) return;
                _queued = true;
            }
            Task.Run(async () =>
            {
                await Task.Delay(3000);
                lock (Gate) _queued = false;
                CheckNow(show);
            });
        }

        /// <summary>Parse "name|category|index" - from the RIGHT: a Wi-Fi name may
        /// itself contain '|' (round-8 N-9). Null when the shape is not what we asked for.</summary>
        internal static NetProfile? ParseProfileLine(string output)
        {
            var parts = output.Trim().Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[^1], out var idx)) return null;
            return new NetProfile { Name = string.Join('|', parts[..^2]), Category = parts[^2], InterfaceIndex = idx };
        }
    }
}

namespace ZephyrPlayer
{
    /// <summary>Latest connection findings, surfaced on the Home status strip and
    /// the Pairing screen's status row. Written once at launch and on network
    /// changes; read on the UI thread.</summary>
    internal static class ConnectionState
    {
        /// <summary>The own discovery port (8490) could not be bound.</summary>
        public static volatile bool DiscoveryOff;

        /// <summary>The Wi-Fi name when Windows treats it as Public (notice-only), else null.</summary>
        public static volatile string? PublicWifiName;

        /// <summary>Raised when DiscoveryOff or PublicWifiName changes (any thread);
        /// UI subscribers must marshal to their dispatcher before touching controls.</summary>
        public static event System.Action? Changed;

        public static void NotifyChanged() => Changed?.Invoke();

        /// <summary>This PC's primary LAN IPv4 (shown on the Pairing screen), else null.
        /// Picked from the interface that owns the default route - not the first RFC1918
        /// address DNS happens to return (Docker/VPN adapters no longer win).</summary>
        public static string? PrimaryIPv4()
        {
            try
            {
                // UDP "connect" to a documentation address: a pure routing-table lookup,
                // no packet leaves the machine. LocalEndPoint is the outbound interface IP.
                using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                s.Connect("192.0.2.1", 9);
                return (s.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
            }
            catch { return null; }
        }
    }
}
