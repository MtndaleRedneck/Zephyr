using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZephyrPlayer
{
    /// <summary>
    /// Answers the phone's "Find my PC" broadcast - the same question the
    /// BingeMote helper listens for, answered with a Zephyr identity so the
    /// phone can tell the apps apart (BingeMote apps match BINGEMOTE_HERE_V1
    /// strictly and ignore us).
    /// 1.2.0: the player answers on its OWN port (8490) and NEVER binds 8489.
    /// The BingeMote helper binds 8489 without address reuse, so on Windows any
    /// socket we bound there - even with ReuseAddress - could make the helper's
    /// bind fail depending on start order. The only coexistence-safe choice is
    /// not to touch 8489 at all: the helper keeps its port unconditionally, in
    /// both start orders (the tests bind a real-shaped helper both ways).
    /// Consequence for phones still on 1.1.0 (8489-only scans): automatic
    /// discovery needs the 1.2.0 phone update; until then the phone's saved PC
    /// address + pairing token keep working, and its setup screen's manual
    /// address entry ("type the address below") covers re-pairing or a changed
    /// PC address - the Pairing screen shows "This PC: <ip>" to type in.
    /// A failed own-port bind surfaces in the UI ("Find my PC is off") instead
    /// of being swallowed.
    /// </summary>
    public sealed class DiscoveryResponder : IDisposable
    {
        public const int OwnPort = 8490;              // Zephyr Player's own port (firewall rule pairs with this)
        public const string RequestMagic = "BINGEMOTE_DISCOVER_V1"; // same question as the helper
        public const string ReplyMagic = "ZEPHYR_PLAYER_HERE_V1";   // our own answer

        private readonly UdpClient _listener;
        private readonly byte[] _reply;
        private readonly CancellationTokenSource _stop = new();

        public DiscoveryResponder(int httpPort, string? pcName = null)
        {
            var name = string.IsNullOrWhiteSpace(pcName) ? Environment.MachineName : pcName;
            _reply = Encoding.UTF8.GetBytes($"{ReplyMagic}|{name}|{httpPort}");
            // The own port binds exclusively: if something else holds 8490 we want to
            // know (the ctor throws, the UI shows "Find my PC is off") rather than
            // silently sharing a port we own. ICMP "port unreachable" replies must not
            // kill the listener either (SIO_UDP_CONNRESET off).
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.ExclusiveAddressUse = true;
            try { sock.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { /* non-Windows */ }
            sock.Bind(new IPEndPoint(IPAddress.Any, OwnPort));
            _listener = new UdpClient { Client = sock };
            _ = Task.Run(() => ReceiveLoop(_listener));
        }

        private async Task ReceiveLoop(UdpClient udp)
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                if (Encoding.UTF8.GetString(r.Buffer).Trim() != RequestMagic) continue;
                try { await udp.SendAsync(_reply, _reply.Length, r.RemoteEndPoint); }
                catch (Exception) { /* one dropped reply costs a scan round, nothing more */ }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
        }
    }
}
