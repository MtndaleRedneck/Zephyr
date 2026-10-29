using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Loopback tests for the library endpoints: auth required, the assigned
    /// folder round-trips, listing is jailed to it (folders first, videos only),
    /// and browse never leaves local drives.
    /// </summary>
    public class LibraryTests : IDisposable
    {
        private const int TestPort = 47898;
        private const string GoodToken = "test-pairing-token";
        private readonly string _root;
        private readonly PlayerSettings _settings;
        private readonly PlaybackServer _server;

        public LibraryTests()
        {
            // Temp library tree: a subfolder, two videos, one non-video.
            _root = Path.Combine(Path.GetTempPath(), "zephyr-lib-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Show"));
            File.WriteAllText(Path.Combine(_root, "b-movie.mkv"), "x");
            File.WriteAllText(Path.Combine(_root, "a-movie.mp4"), "x");
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
            _settings = new PlayerSettings(Path.Combine(_root, "settings.json"));
            _server = new PlaybackServer(
                onPlay: (url, pos, subs) => Task.CompletedTask,
                validateToken: t => t == GoodToken,
                port: TestPort, loopbackOnly: true, settings: _settings);
            _server.Start();
        }

        public void Dispose()
        {
            _server.Dispose();
            try { Directory.Delete(_root, true); } catch { }
        }

        private static HttpClient Client(string? token)
        {
            var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TestPort}") };
            if (token != null) c.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            return c;
        }

        [Fact] public void JunctionEscape_IsNotListed() {
            var outside=Path.Combine(Path.GetTempPath(),"zephyr-outside-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outside);
            var link=Path.Combine(_root,"escape");
            try {if(OperatingSystem.IsWindows()){using var proc=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \""+link+"\" \""+outside+"\""){CreateNoWindow=true,UseShellExecute=false})!;proc.WaitForExit();Assert.Equal(0,proc.ExitCode);}else Directory.CreateSymbolicLink(link,outside);Assert.Equal(404,Library.List(_root,"escape").status);Assert.Equal(200,Library.List(_root,"").status);}
            finally {try{Directory.Delete(link);}catch{}Directory.Delete(outside);}
        }
        [Fact]
        public async Task NoToken_401()
        {
            var resp = await Client(null).GetAsync("/library/list");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task Folder_SetThenGet_RoundTrips()
        {
            var c = Client(GoodToken);
            var resp = await c.PostAsync("/library/folder",
                new StringContent(JsonSerializer.Serialize(new { folder = _root }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var got = JsonDocument.Parse(await c.GetStringAsync("/library/folder")).RootElement;
            Assert.Equal(_root, got.GetProperty("libraryFolder").GetString());
        }

        [Fact]
        public async Task Folder_RejectsMissing()
        {
            var c = Client(GoodToken);
            var resp = await c.PostAsync("/library/folder",
                new StringContent("{\"folder\":\"Z:\\\\no-such-drive-xyz\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        [Fact]
        public async Task List_FoldersFirstThenVideos_NoFolder_400()
        {
            var resp = await Client(GoodToken).GetAsync("/library/list");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        [Fact]
        public async Task List_FoldersFirstThenVideos()
        {
            var c = Client(GoodToken);
            await c.PostAsync("/library/folder",
                new StringContent(JsonSerializer.Serialize(new { folder = _root }), Encoding.UTF8, "application/json"));
            var body = await c.GetStringAsync("/library/list");
            var entries = JsonDocument.Parse(body).RootElement.GetProperty("entries");
            Assert.Equal(3, entries.GetArrayLength());
            Assert.Equal("Show", entries[0].GetProperty("name").GetString());
            Assert.True(entries[0].GetProperty("dir").GetBoolean());
            // natural sort: a-movie before b-movie; notes.txt filtered out
            Assert.Equal("a-movie.mp4", entries[1].GetProperty("name").GetString());
            Assert.Equal("b-movie.mkv", entries[2].GetProperty("name").GetString());
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("atRoot").GetBoolean());
        }

        [Fact]
        public async Task List_JailEscape_404()
        {
            var c = Client(GoodToken);
            await c.PostAsync("/library/folder",
                new StringContent(JsonSerializer.Serialize(new { folder = _root }), Encoding.UTF8, "application/json"));
            var resp = await c.GetAsync("/library/list?path=..");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task Browse_EmptyPath_ListsDrives()
        {
            var body = await Client(GoodToken).GetStringAsync("/library/browse");
            var entries = JsonDocument.Parse(body).RootElement.GetProperty("entries");
            Assert.True(entries.GetArrayLength() >= 1); // CI always has C:
        }

        [Fact]
        public async Task Browse_RejectsRelativePath()
        {
            var resp = await Client(GoodToken).GetAsync("/library/browse?path=temp");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task Browse_SubfoldersOnly_NoFiles()
        {
            var body = await Client(GoodToken).GetStringAsync("/library/browse?path=" + Uri.EscapeDataString(_root));
            var entries = JsonDocument.Parse(body).RootElement.GetProperty("entries");
            Assert.Equal(1, entries.GetArrayLength());
            Assert.Equal("Show", entries[0].GetProperty("name").GetString());
        }
    }
}
