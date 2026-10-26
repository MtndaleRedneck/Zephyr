using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZephyrPlayer
{
    /// <summary>
    /// Answers the phone's "Find my PC" broadcast - the same mechanism the
    /// BingeMote helper uses (UDP 8489, BINGEMOTE_DISCOVER_V1), but replies with
    /// a Zephyr identity so the phone can tell the two apps apart and older
    /// BingeMote apps (which match BINGEMOTE_HERE_V1 strictly) ignore us.
    /// Coexists with the helper on the same PC through address reuse; it never
    /// answers in the helper's name (protocol.md Coexistence).
    /// </summary>
    public sealed class DiscoveryResponder : IDisposable
    {
        public const int DiscoveryPort = 8489;                 // same port as the helper
        public const string RequestMagic = "BINGEMOTE_DISCOVER_V1"; // same question as the helper
        public const string ReplyMagic = "ZEPHYR_PLAYER_HERE_V1";   // our own answer

        private readonly UdpClient _udp;
        private readonly byte[] _reply;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public DiscoveryResponder(int httpPort, string? pcName = null)
        {
            var name = string.IsNullOrWhiteSpace(pcName) ? Environment.MachineName : pcName;
            _reply = Encoding.UTF8.GetBytes($"{ReplyMagic}|{name}|{httpPort}");
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udp = new UdpClient { Client = sock };
            _loop = Task.Run(ReceiveLoop);
        }

        private async Task ReceiveLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                if (Encoding.UTF8.GetString(r.Buffer).Trim() != RequestMagic) continue;
                try { await _udp.SendAsync(_reply, _reply.Length, r.RemoteEndPoint); }
                catch (Exception) { /* one dropped reply costs a scan round, nothing more */ }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
        }
    }
}
