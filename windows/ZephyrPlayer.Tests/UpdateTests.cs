using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Update machinery against a loopback feed: version comparison rules
    /// (drafts/prereleases/other tags skipped, exe asset required, sha parsed)
    /// and download verification (sha mismatch deletes + throws).
    /// </summary>
    public class UpdateTests : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _base;
        private string _releasesJson = "[]";
        private byte[] _exeBytes = Array.Empty<byte>();

        public UpdateTests()
        {
            var port = 47895;
            _base = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(_base + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    var bytes = ctx.Request.Url.AbsolutePath == "/exe"
                        ? _exeBytes
                        : Encoding.UTF8.GetBytes(_releasesJson);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }

        private void Feed(string releasesJson) => _releasesJson = releasesJson;

        private static string Release(string tag, string assetName, string? digest = null, bool draft = false, bool prerelease = false) =>
            "{\"tag_name\":\"" + tag + "\",\"draft\":" + draft.ToString().ToLower() +
            ",\"prerelease\":" + prerelease.ToString().ToLower() +
            ",\"assets\":[{\"name\":\"" + assetName + "\",\"browser_download_url\":\"http://127.0.0.1:47895/exe\"" +
            (digest != null ? ",\"digest\":\"sha256:" + digest + "\"" : "") + "}]}";

        [Fact]
        public void NewerPlayerTag_WithExe_Wins()
        {
            Feed("[" + Release("player-v1.2.0", "ZephyrPlayer-1.2.0.exe", new string('a', 64)) + "]");
            var hit = UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases");
            Assert.NotNull(hit);
            Assert.Equal("1.2.0", hit!.Value.Version);
            Assert.Equal(new string('a', 64), hit.Value.Sha256);
        }

        [Fact]
        public void OlderOrEqual_Skipped()
        {
            Feed("[" + Release("player-v0.9.0", "ZephyrPlayer-0.9.0.exe") + "," + Release("player-v1.0.0", "ZephyrPlayer-1.0.0.exe") + "]");
            Assert.Null(UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases"));
        }

        [Fact]
        public void Drafts_Prereleases_OtherTags_Skipped()
        {
            Feed("[" + Release("player-v2.0.0", "ZephyrPlayer-2.0.0.exe", draft: true) + ","
                 + Release("player-v2.0.0", "ZephyrPlayer-2.0.0.exe", prerelease: true) + ","
                 + Release("pc-v9.9.9", "ZephyrPlayer-9.9.9.exe") + ","
                 + Release("v2.0.0", "ZephyrPlayer-2.0.0.exe") + "]");
            Assert.Null(UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases"));
        }

        [Fact]
        public void NewerWithoutExe_Skipped()
        {
            Feed("[" + Release("player-v2.0.0", "checksums.txt") + "]");
            Assert.Null(UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases"));
        }

        [Fact]
        public async Task Download_VerifiesSha256()
        {
            _exeBytes = Encoding.UTF8.GetBytes("fake installer payload");
            using var sha = SHA256.Create();
            var hex = Convert.ToHexString(sha.ComputeHash(_exeBytes)).ToLowerInvariant();
            var path = await SelfUpdate.Download("1.2.0", _base + "/exe", hex, hostAllowed: _ => true);
            Assert.Equal(_exeBytes.Length, new System.IO.FileInfo(path).Length);
            System.IO.File.Delete(path);
        }

        [Fact]
        public async Task Download_BadSha_DeletesAndThrows()
        {
            _exeBytes = Encoding.UTF8.GetBytes("fake installer payload");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SelfUpdate.Download("1.2.0", _base + "/exe", new string('b', 64), hostAllowed: _ => true));
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZephyrPlayer-1.2.0.exe")));
        }

        [Fact]
        public async Task Download_UntrustedHost_Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SelfUpdate.Download("1.2.0", "http://evil.example.com/x.exe", null));
        }
    }
}
