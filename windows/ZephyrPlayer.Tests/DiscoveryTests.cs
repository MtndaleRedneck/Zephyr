using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// "Find my PC": the phone's broadcast question must get OUR identity back
    /// (never the helper's), with the player's real HTTP port.
    public class DiscoveryTests
    {
        /// Helper-style responder used to prove coexistence: same port, same
        /// question, the helper's own reply identity.
        private sealed class FakeHelper : IDisposable
        {
            private readonly UdpClient _udp;
            private readonly byte[] _reply = Encoding.UTF8.GetBytes("BINGEMOTE_HERE_V1|HelperPC|8488");
            private readonly CancellationTokenSource _stop = new();
            public FakeHelper()
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.Bind(new IPEndPoint(IPAddress.Any, 8489));
                _udp = new UdpClient { Client = sock };
                _ = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        UdpReceiveResult r;
                        try { r = await _udp.ReceiveAsync(_stop.Token); }
                        catch (Exception) { return; }
                        if (Encoding.UTF8.GetString(r.Buffer).Trim() != "BINGEMOTE_DISCOVER_V1") continue;
                        try { await _udp.SendAsync(_reply, _reply.Length, r.RemoteEndPoint); } catch (Exception) { }
                    }
                });
            }
            public void Dispose() { _stop.Cancel(); _udp.Dispose(); }
        }

        [Fact]
        public async Task CoexistsWithHelperOnTheSamePort()
        {
            // Both apps answering one broadcast on the same PC: the phone must
            // see BOTH identities. Windows broadcast delivery semantics - this
            // test only means anything on the Windows CI runner.
            if (!OperatingSystem.IsWindows()) return;
            using var helper = new FakeHelper();
            using var player = new DiscoveryResponder(47823, "TestPC");
            using var asker = new UdpClient();
            asker.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            asker.Client.ReceiveTimeout = 250;
            var question = Encoding.UTF8.GetBytes("BINGEMOTE_DISCOVER_V1");
            var target = new IPEndPoint(IPAddress.Broadcast, 8489);
            var replies = new HashSet<string>();
            var deadline = DateTime.UtcNow.AddSeconds(4);
            var sends = 0;
            while (DateTime.UtcNow < deadline && replies.Count < 2)
            {
                if (sends < 6) { try { await asker.SendAsync(question, question.Length, target); } catch (Exception) { } sends++; }
                try
                {
                    var r = await asker.ReceiveAsync();
                    replies.Add(Encoding.UTF8.GetString(r.Buffer));
                }
                catch (Exception) { /* wait for both */ }
            }
            Assert.Contains("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", replies);
            Assert.Contains("BINGEMOTE_HERE_V1|HelperPC|8488", replies);
        }

        [Fact]
        public async Task AnswersDiscoverWithZephyrIdentity()
        {
            using var responder = new DiscoveryResponder(47823, "TestPC");
            using var asker = new UdpClient();
            asker.Client.ReceiveTimeout = 3000;
            var question = Encoding.UTF8.GetBytes("BINGEMOTE_DISCOVER_V1");
            await asker.SendAsync(question, question.Length, new IPEndPoint(IPAddress.Loopback, 8489));
            var reply = await asker.ReceiveAsync();
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823",
                Encoding.UTF8.GetString(reply.Buffer));
        }

        [Fact]
        public async Task IgnoresOtherTraffic()
        {
            using var responder = new DiscoveryResponder(47823, "TestPC");
            using var asker = new UdpClient();
            asker.Client.ReceiveTimeout = 700;
            var noise = Encoding.UTF8.GetBytes("BINGEMOTE_HERE_V1|not-a-question|1");
            await asker.SendAsync(noise, noise.Length, new IPEndPoint(IPAddress.Loopback, 8489));
            await Assert.ThrowsAnyAsync<Exception>(async () => await asker.ReceiveAsync());
        }
    }
}
