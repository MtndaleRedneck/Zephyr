using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Loopback integration tests for the /play handoff: auth is mandatory,
    /// the payload parses into the handler, unknown paths 404.
    /// </summary>
    public class PlaybackServerTests
    {
        private const int TestPort = 47899;
        private const string GoodToken = "test-pairing-token";

        private const string MintedToken = "minted-pairing-token";

        private static (PlaybackServer server, Func<Task<(int, string)>> postFactory) Start(
            out TaskCompletionSource<(string url, long? pos, string[] subs)> received, bool withPairing = false)
        {
            var (server, _) = GateStart(out received, withPairing, out _);
            return (server, null!);
        }

        /// <summary>Real PairingGate behind the server so the tests exercise the actual
        /// lockout/expiry/one-shot machinery, not a stub. gateCode: show a code now.</summary>
        private static (PlaybackServer server, PairingGate? gate) GateStart(
            out TaskCompletionSource<(string url, long? pos, string[] subs)> received,
            bool withPairing, out string? gateCode, Func<DateTime>? clock = null, int phoneCount = 0)
        {
            var tcs = new TaskCompletionSource<(string, long?, string[])>();
            received = tcs;
            var gate = withPairing ? new PairingGate(clock) : null;
            gateCode = gate?.NewCode();
            var server = new PlaybackServer(
                onPlay: (url, pos, subs) => { tcs.TrySetResult((url, pos, subs)); return Task.CompletedTask; },
                validateToken: t => t == GoodToken || (withPairing && t == MintedToken),
                port: TestPort, loopbackOnly: true,
                onPair: withPairing
                    ? (code =>
                    {
                        var outcome = gate!.TryPair(code, phoneCount, () => MintedToken, out var token);
                        return (outcome, token);
                    })
                    : (Func<string, (PairOutcome, string?)>?)null);
            server.Start();
            return (server, gate);
        }

        private static async Task<HttpResponseMessage> Pair(System.Net.Http.HttpClient c, string code)
            => await c.PostAsync("/pair", new StringContent("{\"code\":\"" + code + "\"}", Encoding.UTF8, "application/json"));

        private static HttpClient Client(string? token)
        {
            var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TestPort}") };
            if (token != null) c.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            return c;
        }

        [Fact]
        public async Task NoToken_401()
        {
            var (server, _) = Start(out _);
            using (server)
            {
                var resp = await Client(null).PostAsync("/play", new StringContent("{\"url\":\"http://x/1\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
        }

        [Fact]
        public async Task BadToken_401()
        {
            var (server, _) = Start(out _);
            using (server)
            {
                var resp = await Client("wrong").PostAsync("/play", new StringContent("{\"url\":\"http://x/1\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
        }

        [Fact]
        public async Task ValidPlay_ParsesIntoHandler()
        {
            var (server, _) = Start(out var received);
            using (server)
            {
                var body = "{\"url\":\"http://10.0.0.5:8080/s/tok/1\",\"position\":95,\"subtitles\":[\"http://10.0.0.5:8080/s/tok/1/sub/0\"]}";
                var resp = await Client(GoodToken).PostAsync("/play", new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var done = await Task.WhenAny(received.Task, Task.Delay(5000));
                Assert.Same(received.Task, done);
                Assert.Equal("http://10.0.0.5:8080/s/tok/1", received.Task.Result.url);
                Assert.Equal(95, received.Task.Result.pos);
                Assert.Equal(new[] { "http://10.0.0.5:8080/s/tok/1/sub/0" }, received.Task.Result.subs);
            }
        }

        [Fact]
        public async Task Pair_WrongCode_403_NoToken()
        {
            var (server, _) = GateStart(out _, withPairing: true, out _);
            using (server)
            {
                var resp = await Pair(Client(null), "000000");
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            }
        }

        [Fact]
        public async Task Pair_RightCode_MintsUsableToken()
        {
            var (server, _) = GateStart(out var received, withPairing: true, out var code);
            using (server)
            {
                var resp = await Pair(Client(null), code!);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Contains(MintedToken, await resp.Content.ReadAsStringAsync());
                // The minted token must open /play immediately (no second step).
                var play = await Client(MintedToken).PostAsync("/play", new StringContent("{\"url\":\"http://x/1\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, play.StatusCode);
                // One-shot: the same code must not mint a second token.
                var again = await Pair(Client(null), code!);
                Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
            }
        }

        [Fact]
        public async Task Pair_FiveWrongCodes_LocksForFiveMinutes()
        {
            var (server, _) = GateStart(out _, withPairing: true, out var code);
            using (server)
            {
                for (var i = 0; i < PairingGate.MaxFailures; i++)
                    Assert.Equal(HttpStatusCode.Forbidden, (await Pair(Client(null), "000000")).StatusCode);
                // Locked: even the RIGHT code now gets 429 until the lock runs out
                // (1.2.0: time-based - the phone says "try again in a few minutes",
                // never "restart the PC").
                Assert.Equal((HttpStatusCode)429, (await Pair(Client(null), "000000")).StatusCode);
                Assert.Equal((HttpStatusCode)429, (await Pair(Client(null), code!)).StatusCode);
                var body = await (await Pair(Client(null), code!)).Content.ReadAsStringAsync();
                Assert.Contains("few minutes", body);
                Assert.DoesNotContain("Restart", body);
            }
        }

        [Fact]
        public async Task Pair_GuessWhileNoCodeShowing_DoesNotCountTowardLock()
        {
            // Gate with NO code on screen: a LAN guest's guesses must not be able to
            // lock pairing before the user ever starts it (helper parity).
            var tcs = new TaskCompletionSource<(string, long?, string[])>();
            var gate = new PairingGate();
            var server = new PlaybackServer(
                onPlay: (u, p, s) => Task.CompletedTask,
                validateToken: t => false,
                port: TestPort, loopbackOnly: true,
                onPair: code =>
                {
                    var outcome = gate.TryPair(code, 0, () => MintedToken, out var token);
                    return (outcome, token);
                });
            using (server)
            {
                server.Start();
                for (var i = 0; i < PairingGate.MaxFailures + 3; i++)
                    Assert.Equal(HttpStatusCode.Forbidden, (await Pair(Client(null), "000000")).StatusCode);
                // Not locked: showing a code now and typing it right still pairs.
                var code = gate.NewCode();
                Assert.Equal(HttpStatusCode.OK, (await Pair(Client(null), code)).StatusCode);
            }
        }

        [Fact]
        public async Task Pair_ExpiredCode_410_DistinctFromWrongCode()
        {
            // Settled distinction: an EXPIRED code tells the phone "use the new
            // code on the PC screen"; a WRONG code stays 403 "bad code".
            var now = DateTime.UtcNow;
            var (server, _) = GateStart(out _, withPairing: true, out var code, clock: () => now);
            using (server)
            {
                now += PairingGate.CodeLifetime + TimeSpan.FromMinutes(1);
                var resp = await Pair(Client(null), code!);
                Assert.Equal((HttpStatusCode)410, resp.StatusCode);
                Assert.Contains("code expired", await resp.Content.ReadAsStringAsync());
                // A fresh code + one digit off is still a plain wrong code (403).
                now += TimeSpan.FromMinutes(1);
                var resp2 = await Pair(Client(null), "000000");
                // No live code after the expiry consumed it: 403 "no code".
                Assert.Equal(HttpStatusCode.Forbidden, resp2.StatusCode);
            }
        }

        [Fact]
        public async Task Pair_PhoneCap_409()
        {
            var (server, _) = GateStart(out _, withPairing: true, out var code, phoneCount: PairingGate.MaxPhones);
            using (server)
            {
                Assert.Equal((HttpStatusCode)409, (await Pair(Client(null), code!)).StatusCode);
            }
        }

        [Fact]
        public async Task Pair_RouteExistsOnlyWhenWired()
        {
            // Production always wires pairing; if it is absent the route must not
            // fall through to minting anything - 403, not a token.
            var (server, _) = Start(out _);
            using (server)
            {
                var resp = await Pair(Client(null), "246810");
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            }
        }

        [Fact]
        public async Task UnknownPath_404_WithValidToken()
        {
            var (server, _) = Start(out _);
            using (server)
            {
                var resp = await Client(GoodToken).PostAsync("/nope", new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            }
        }
    }
}
