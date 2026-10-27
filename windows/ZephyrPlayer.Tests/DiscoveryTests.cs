using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// "Find my PC" 1.2.0: the player answers on its OWN port (8490) and never
    /// binds 8489. The BingeMote helper binds 8489 without address reuse
    /// (BingeMote helper/HttpServer.cs:497), so on Windows ANY socket we bound
    /// there could make the helper's bind fail depending on start order. The
    /// tests below bind a fake helper with the REAL helper's bind shape in both
    /// start orders and prove the helper always keeps its port (teardown
    /// pairing finding B; settled requirement: BingeMote keeps 8489
    /// unconditionally, Zephyr Player gets its own dedicated port).
    ///
    /// Verified phone behavior (android sources): the Zephyr phone accepts only
    /// ZEPHYR_PLAYER_HERE_V1 replies (ZephyrClient.REPLY_MAGIC); the BingeMote
    /// phone accepts only BINGEMOTE_HERE_V1 (HelperClient.REPLY_MAGIC).
    /// Consequence for phones still on 1.1.0 (8489-only scans): they cannot
    /// auto-discover a 1.2.0 player. Their saved PC address + pairing token
    /// keep working (Store.zephyrHost/zephyrToken persist; the player's
    /// paired.json migrates), and the 1.1.0 setup screen's manual address
    /// entry covers a changed PC address or a fresh pair until the phone
    /// updates to 1.2.0. That sequencing consequence is called out in
    /// docs/protocol.md and the release notes.
    public class DiscoveryTests
    {
        /// Helper-style responder with the REAL helper's bind shape:
        /// `new UdpClient(8489)` - no ReuseAddress (helper/HttpServer.cs:497).
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
            using var responder = new DiscoveryResponder(47823, "TestPC");
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", await AskOne(8490));
        }

        [Fact]
        public async Task Never_Answers_On_The_Helpers_Port()
        {
            // The never-bind guarantee as a regression pin: with ONLY the player
            // running, 8489 stays silent. (Old phones then rely on their saved
            // address or manual entry until they update - see class comment.)
            if (!OperatingSystem.IsWindows()) return;
            using var player = new DiscoveryResponder(47823, "TestPC");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskOne(8489, timeoutMs: 800));
        }

        [Fact]
        public async Task Player_First_Helper_Still_Binds_And_Answers()
        {
            // The settled coexistence outcome: starting the player FIRST must not
            // disturb the helper's exclusive 8489 bind - the helper's phone keeps
            // working no matter what order the apps start in.
            if (!OperatingSystem.IsWindows()) return;
            using var player = new DiscoveryResponder(47823, "TestPC");
            using var helper = new FakeHelper(); // would throw if the player held 8489
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", await AskOne(8490));
            Assert.Equal("BINGEMOTE_HERE_V1|HelperPC|8488", await AskOne(8489));
        }

        [Fact]
        public async Task Helper_First_Player_Still_Answers_On_Own_Port()
        {
            // Other order: the helper holds 8489; the player's own port answers.
            if (!OperatingSystem.IsWindows()) return;
            using var helper = new FakeHelper();
            using var player = new DiscoveryResponder(47823, "TestPC");
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", await AskOne(8490));
            Assert.Equal("BINGEMOTE_HERE_V1|HelperPC|8488", await AskOne(8489));
        }

        [Fact]
        public async Task IgnoresOtherTraffic()
        {
            using var responder = new DiscoveryResponder(47823, "TestPC");
            using var asker = new UdpClient();
            var noise = Encoding.UTF8.GetBytes("BINGEMOTE_HERE_V1|not-a-question|1");
            await asker.SendAsync(noise, noise.Length, new IPEndPoint(IPAddress.Loopback, 8490));
            var question = Encoding.UTF8.GetBytes("BINGEMOTE_DISCOVER_V1");
            await asker.SendAsync(question, question.Length, new IPEndPoint(IPAddress.Loopback, 8490));
            using var rc = new CancellationTokenSource(3000);
            var reply = await asker.ReceiveAsync(rc.Token).AsTask();
            Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TestPC|47823", Encoding.UTF8.GetString(reply.Buffer));
        }
    }
}
