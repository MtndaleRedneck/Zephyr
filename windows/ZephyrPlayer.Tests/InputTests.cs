using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Loopback tests for the input receiver routes: auth required, each route
    /// reaches the sink with parsed values, out-of-contract names 400. SendInput
    /// itself can't be verified headless - the sink seam is what CI checks.
    /// </summary>
    public class InputTests
    {
        private const int TestPort = 47897;
        private const string GoodToken = "test-pairing-token";

        private sealed class Recorder : IInputSink
        {
            public readonly List<string> Events = new();
            public void Move(float dx, float dy) => Events.Add($"move {dx} {dy}");
            public void Button(string button, bool down) => Events.Add($"button {button} {down}");
            public void Scroll(float dy) => Events.Add($"scroll {dy}");
            public void Zoom(float delta) => Events.Add($"zoom {delta}");
            public void TypeText(string text) => Events.Add($"type {text}");
            public void NamedKey(string key) => Events.Add($"key {key}");
        }

        private static (PlaybackServer server, Recorder rec) Start(bool withSink = true)
        {
            var rec = new Recorder();
            var server = new PlaybackServer(
                onPlay: (url, pos, subs) => Task.CompletedTask,
                validateToken: t => t == GoodToken,
                port: TestPort, loopbackOnly: true,
                input: withSink ? rec : null);
            server.Start();
            return (server, rec);
        }

        private static HttpClient Client(string? token)
        {
            var c = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{TestPort}") };
            if (token != null) c.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            return c;
        }

        private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

        [Fact]
        public async Task NoToken_401()
        {
            var (server, _) = Start();
            using (server)
            {
                var resp = await Client(null).PostAsync("/input/move", Json("{\"dx\":1,\"dy\":2}"));
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
        }

        [Fact]
        public async Task NoSink_404()
        {
            var (server, _) = Start(withSink: false);
            using (server)
            {
                var resp = await Client(GoodToken).PostAsync("/input/move", Json("{\"dx\":1,\"dy\":2}"));
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            }
        }

        [Fact]
        public async Task Move_Button_Scroll_Zoom_ReachSink()
        {
            var (server, rec) = Start();
            using (server)
            {
                var c = Client(GoodToken);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/input/move", Json("{\"dx\":3.5,\"dy\":-2}"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/input/button", Json("{\"button\":\"right\",\"down\":true}"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/input/scroll", Json("{\"dy\":1.25}"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/input/zoom", Json("{\"delta\":-0.5}"))).StatusCode);
                Assert.Equal(new[] { "move 3.5 -2", "button right True", "scroll 1.25", "zoom -0.5" }, rec.Events);
            }
        }

        [Fact]
        public async Task Key_And_Type_ReachSink()
        {
            var (server, rec) = Start();
            using (server)
            {
                var c = Client(GoodToken);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/input/key", Json("{\"key\":\"enter\"}"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/input/type", Json("{\"text\":\"hello\"}"))).StatusCode);
                Assert.Equal(new[] { "key enter", "type hello" }, rec.Events);
            }
        }

        [Fact]
        public async Task UnknownKey_400_UnknownButton_400()
        {
            var (server, rec) = Start();
            using (server)
            {
                var c = Client(GoodToken);
                Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/input/key", Json("{\"key\":\"f13\"}"))).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/input/button", Json("{\"button\":\"middle\",\"down\":true}"))).StatusCode);
                Assert.Empty(rec.Events);
            }
        }
    }
}
