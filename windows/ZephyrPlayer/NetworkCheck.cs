namespace ZephyrPlayer
{
    /// <summary>Latest connection findings, surfaced on the Home status strip and
    /// the Pairing screen's status row. Written once at launch and on network
    /// changes; read on the UI thread.</summary>
    internal static class ConnectionState
    {
        /// <summary>The own discovery port (8490) could not be bound.</summary>
        public static volatile bool DiscoveryOff;

        /// <summary>Raised when DiscoveryOff or the network changes (any thread);
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
