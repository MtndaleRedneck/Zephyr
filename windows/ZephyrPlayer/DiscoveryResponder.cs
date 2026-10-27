using System;
using System.Collections.Generic;
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
    /// phone can tell the apps apart (older BingeMote apps match
    /// BINGEMOTE_HERE_V1 strictly and ignore us).
    /// 1.2.0: the player moves to its OWN discovery port (8490) so the BingeMote
    /// helper holding 8489 without sharing can no longer take "Find my PC" down
    /// with it. The helper keeps 8489. For phones still on 1.1.0 (which only
    /// scan 8489), the player ALSO answers 8489 best-effort when that port is
    /// free; failure there is silent by design - the own port is authoritative.
    /// A failed own-port bind surfaces in the UI ("Find my PC is off") instead
    /// of being swallowed.
    /// </summary>
    public sealed class DiscoveryResponder : IDisposable
    {
        public const int OwnPort = 8490;              // Zephyr Player's own port (firewall rule pairs with this)
        public const int LegacyPort = 8489;           // the helper's port; answered best-effort for old phones
        public const string RequestMagic = "BINGEMOTE_DISCOVER_V1"; // same question as the helper
        public const string ReplyMagic = "ZEPHYR_PLAYER_HERE_V1";   // our own answer

        private readonly List<UdpClient> _listeners = new();
        private readonly byte[] _reply;
        private readonly CancellationTokenSource _stop = new();

        /// <summary>True when the best-effort legacy (8489) listener is up.</summary>
        public bool AnswersLegacyPort { get; }

        public DiscoveryResponder(int httpPort, string? pcName = null, bool answerLegacyPort = true)
        {
            var name = string.IsNullOrWhiteSpace(pcName) ? Environment.MachineName : pcName;
            _reply = Encoding.UTF8.GetBytes($"{ReplyMagic}|{name}|{httpPort}");
            // The own port binds WITHOUT address reuse: if something else holds
            // 8490 we want to know (the ctor throws, the UI shows "Find my PC
            // is off") rather than silently sharing a port we own.
            _listeners.Add(Bind(OwnPort, reuse: false));
            if (answerLegacyPort)
            {
                // Legacy phones scan 8489 only. The helper binds that port
                // without sharing, so this can fail depending on start order;
                // either way the own port above still answers new phones.
                try
                {
                    _listeners.Add(Bind(LegacyPort, reuse: true));
                    AnswersLegacyPort = true;
                }
                catch (Exception) { AnswersLegacyPort = false; }
            }
            foreach (var udp in _listeners) _ = Task.Run(() => ReceiveLoop(udp));
        }

        private static UdpClient Bind(int port, bool reuse)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (reuse) sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, port));
            return new UdpClient { Client = sock };
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
            foreach (var udp in _listeners) udp.Dispose();
        }
    }
}
