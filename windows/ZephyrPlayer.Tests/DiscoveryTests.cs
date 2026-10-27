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
    /// "Find my PC" 1.2.0: the player owns UDP 8490 (the helper keeps 8489) and
    /// answers 8489 only best-effort for phones still on 1.1.0. The coexistence
    /// test now binds the fake helper EXACTLY like the real one (no ReuseAddress)
    /// and runs in both start orders (teardown pairing finding B).
    public class DiscoveryTests
    {
        /// Helper-style responder with the REAL helper's bind shape:
        /// `new UdpClient(8489)` - no address reuse (helper/HttpServer.cs:497).
        private sealed class FakeHelper : IDisposable
        {
            private readonly UdpClient _udp;
            private readonly byte[] _reply = Encoding.UTF8.GetBytes("BINGEMOTE_HERE_V1|HelperPC|8488");
            private readonly CancellationTokenSource _stop = new();
            public FakeHelper()
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.Bind(new IPEndPoint(IPAddress.Any, 8489)); // NO ReuseAddress - the real helper's shape
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

        private static async Task<string> AskOne(int port, int timeoutMs = 3000)
        {
            using var asker = new UdpClient();
            var question = Encoding.UTF8.GetBytes("BINGEMOTE_DISCOVER_V1");
            await asker.SendAsync(question, question.Length, new IPEndPoint(IPAddress.Loopback, port));
            using var rc = new CancellationTokenSource(timeoutMs);
            var reply = await asker.ReceiveAsync(rc.Token).AsTask();
            return Encoding.UTF8.GetString(reply.Buffer);
        }

        [Fact]
        public async Task Answers_On_Own_Port()
        {
            using var responder = new DiscoveryResponder(47823, "TestPC", answerLegacyPort: false);
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", await AskOne(8490));
        }

        [Fact]
        public async Task Answers_Legacy_Port_When_Free()
        {
            // No helper running: old phones (1.1.0, 8489-only) still find the player.
            using var responder = new DiscoveryResponder(47823, "TestPC");
            Assert.True(responder.AnswersLegacyPort);
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", await AskOne(8489));
        }

        [Fact]
        public async Task Helper_First_Player_Still_Answers_On_Own_Port()
        {
            // The real-helper bind shape holds 8489 exclusively; the player's own
            // port must still answer. Windows semantics - meaningful on the runner.
            if (!OperatingSystem.IsWindows()) return;
            using var helper = new FakeHelper();
            using var player = new DiscoveryResponder(47823, "TestPC");
            Assert.False(player.AnswersLegacyPort); // helper holds it; that's fine now
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", await AskOne(8490));
        }

        [Fact]
        public async Task Player_First_Helper_Still_Binds_And_Answers()
        {
            // Both start orders: with the player on its own port, the helper's
            // exclusive 8489 bind is never disturbed either.
            if (!OperatingSystem.IsWindows()) return;
            using var player = new DiscoveryResponder(47823, "TestPC");
            using var helper = new FakeHelper();
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", await AskOne(8490));
            Assert.Equal("BINGEMOTE_HERE_V1|HelperPC|8488", await AskOne(8489));
        }

        [Fact]
        public async Task IgnoresOtherTraffic()
        {
            using var responder = new DiscoveryResponder(47823, "TestPC", answerLegacyPort: false);
            using var asker = new UdpClient();
            var noise = Encoding.UTF8.GetBytes("BINGEMOTE_HERE_V1|not-a-question|1");
            await asker.SendAsync(noise, noise.Length, new IPEndPoint(IPAddress.Loopback, 8490));
            // No reply may arrive: bound the wait with a token (async receives
            // ignore ReceiveTimeout) and require it to time out.
            using var rc = new CancellationTokenSource(700);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await asker.ReceiveAsync(rc.Token).AsTask());
        }
    }
}
