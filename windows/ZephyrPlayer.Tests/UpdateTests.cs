using System;
using System.Linq;
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
        private int _statusCode = 200;
        private string? _redirectLocation;
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
                    ctx.Response.StatusCode = _statusCode;
                    if (_redirectLocation != null) ctx.Response.RedirectLocation = _redirectLocation;
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
        public void MissingDigest_NotOffered()
        {
            Feed("["+Release("player-v9.0.0","Zephyr-PC-9.0.0.exe")+"]");
            Assert.Equal("unverified_release",UpdateCheck.CheckOutcomeOrThrow("1.0.0",_base+"/releases"));
            Assert.Null(UpdateState.AvailableOffer);
        }
        [Fact]
        public void NewerPlayerTag_WithExe_Wins()
        {
            Feed("[" + Release("player-v1.2.0", "Zephyr-PC-1.2.0.exe", new string('a', 64)) + "]");
            var hit = UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases");
            Assert.NotNull(hit);
            Assert.Equal("1.2.0", hit!.Value.Version);
            Assert.Equal(new string('a', 64), hit.Value.Sha256);
        }

        [Fact]
        public void OlderOrEqual_Skipped()
        {
            Feed("[" + Release("player-v0.9.0", "Zephyr-PC-0.9.0.exe") + "," + Release("player-v1.0.0", "Zephyr-PC-1.0.0.exe") + "]");
            Assert.Null(UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases"));
        }

        [Fact]
        public void Drafts_Prereleases_OtherTags_Skipped()
        {
            Feed("[" + Release("player-v2.0.0", "Zephyr-PC-2.0.0.exe", draft: true) + ","
                 + Release("player-v2.0.0", "Zephyr-PC-2.0.0.exe", prerelease: true) + ","
                 + Release("pc-v9.9.9", "Zephyr-PC-9.9.9.exe") + ","
                 + Release("v2.0.0", "Zephyr-PC-2.0.0.exe") + "]");
            Assert.Null(UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases"));
        }

        [Fact]
        public void NewerWithoutExe_Skipped()
        {
            Feed("[" + Release("player-v2.0.0", "checksums.txt") + "]");
            Assert.Null(UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases"));
        }

        [Fact]
        public void FeedStates_SeparateMissingCurrentNewerAndFailure()
        {
            Feed("[]");
            Assert.Equal("no_release", UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            Feed("[" + Release("player-v1.0.0", "Zephyr-PC-1.0.0.exe") + "]");
            Assert.Equal("up_to_date", UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            Feed("[" + Release("player-v1.2.0", "Zephyr-PC-1.2.0.exe", new string('b',64)) + "]");
            Assert.Equal("available: 1.2.0", UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            // Installed AHEAD of the feed (unpublished build) is its own state - never "latest published".
            Assert.Equal("ahead_of_feed", UpdateCheck.CheckOutcomeOrThrow("1.3.0", _base + "/releases"));
            Feed("not json");
            Assert.ThrowsAny<Exception>(() => UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            Assert.Equal("No published player installer available yet.", UpdateState.Display("no_release"));
            Assert.DoesNotContain("up_to_date", UpdateState.Display("up_to_date"));
            Assert.Equal("You're up to date.", UpdateState.Display("up_to_date"));
            Assert.Equal("No newer version is available.", UpdateState.Display("ahead_of_feed"));
            Assert.DoesNotContain("latest published", UpdateState.Display("ahead_of_feed"));
            Assert.DoesNotContain("latest published", UpdateState.Display("up_to_date"));
        }

        [Fact]
        public void MissingOrFailedFeed_NeverReportsCurrent()
        {
            _statusCode = 404;
            Assert.Throws<HttpRequestException>(() => UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            _statusCode = 503;
            Assert.Throws<HttpRequestException>(() => UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            Assert.Equal("Could not check for updates. Try again later.", UpdateState.Display("failed: HttpRequestException"));
        }

        [Fact]
        public void Feed_RejectsWrongAppAssets_ChoosesHighestVersion()
        {
            Feed("[" + Release("player-v9.0.0", "BingeMote-PC-9.0.0.exe") + "," +
                Release("player-v1.1.0", "Zephyr-PC-1.1.0.exe") + "," +
                Release("player-v1.3.0", "Zephyr-PC-1.3.0.exe") + "]");
            Assert.Equal("1.3.0", UpdateCheck.LatestNewerThanOrThrow("1.0.0", _base + "/releases")!.Value.Version);
        }

        [Fact]
        public async Task Download_RedirectCannotBypassHostValidation()
        {
            _statusCode = 302;
            _redirectLocation = "https://untrusted.invalid/payload.exe";
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SelfUpdate.Download("1.2.0", _base + "/exe", new string('b', 64), hostAllowed: h => h == "127.0.0.1"));
            Assert.Contains("not trusted", ex.Message);
        }

        [Fact]
        public void LaunchOffer_IsAvailable_ThenClearedForNoReleaseOrFailedFeed()
        {
            Feed("[" + Release("player-v1.2.0", "Zephyr-PC-1.2.0.exe", new string('b', 64)) + "]");
            Assert.Equal("available: 1.2.0", UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            Assert.Equal("1.2.0", UpdateState.AvailableOffer?.Version);
            Feed("[]");
            Assert.Equal("no_release", UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            Assert.Null(UpdateState.AvailableOffer);
            Feed("bad json");
            Assert.ThrowsAny<Exception>(() => UpdateCheck.CheckOutcomeOrThrow("1.0.0", _base + "/releases"));
            Assert.Null(UpdateState.AvailableOffer);
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
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Zephyr-PC-1.2.0.exe")));
        }

        [Fact]
        public async Task Download_MissingDigest_FailsClosed()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SelfUpdate.Download("1.2.0", _base + "/exe", null, hostAllowed: _ => true));
        }
        [Fact]
        public async Task Launch_RechecksChangedFile()
        {
            _exeBytes = Encoding.UTF8.GetBytes("verified installer");
            var hex = Convert.ToHexString(SHA256.HashData(_exeBytes));
            var path = await SelfUpdate.Download("1.2.0", _base + "/exe", hex, hostAllowed: _ => true);
            System.IO.File.WriteAllText(path, "tampered");
            Assert.Throws<InvalidOperationException>(() => SelfUpdate.OpenVerifiedForLaunch(path));
        }

        private static string UpdatesRoot =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZephyrPlayer", "Updates");

        private static string[] UpdateDirs() => System.IO.Directory.Exists(UpdatesRoot) ? System.IO.Directory.GetDirectories(UpdatesRoot) : new string[0];

        [Fact]
        public async Task Download_DeclaredLengthOverCap_RefusedBeforeWriting()
        {
            var oldLimit = SelfUpdate.MaxInstallerBytes;
            SelfUpdate.MaxInstallerBytes = 100;
            try
            {
                _exeBytes = new byte[200];
                var before = UpdateDirs();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    SelfUpdate.Download("1.2.0", _base + "/exe", new string('b', 64), hostAllowed: _ => true));
                Assert.Contains("too large", ex.Message);
                Assert.Empty(UpdateDirs().Except(before)); // no partial download directory survives
            }
            finally { SelfUpdate.MaxInstallerBytes = oldLimit; }
        }

        [Fact]
        public async Task Download_BodyGrowsPastCap_DeletesAndThrows()
        {
            var oldLimit = SelfUpdate.MaxInstallerBytes;
            SelfUpdate.MaxInstallerBytes = 100;
            try
            {
                _exeBytes = new byte[64 * 1024 + 200]; // chunk larger than the cap: the running-total check fires
                var before = UpdateDirs();
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    SelfUpdate.Download("1.2.0", _base + "/exe", new string('b', 64), hostAllowed: _ => true));
                var survivors = UpdateDirs().Except(before).ToArray();
                Assert.Empty(survivors); // the partial file's directory is gone...
                foreach (var dir in survivors)
                    Assert.False(System.IO.File.Exists(System.IO.Path.Combine(dir, "installer.exe"))); // ...and no partial installer.exe remains
            }
            finally { SelfUpdate.MaxInstallerBytes = oldLimit; }
        }

        [Fact]
        public async Task Download_UntrustedHost_Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SelfUpdate.Download("1.2.0", "http://evil.example.com/x.exe", null));
        }

        // ---------- CP4 item 94: the canonical repository and its historical name ----------

        /// <summary>The live feed's shape, read from
        /// https://api.github.com/repos/MtndaleRedneck/Zephyr/releases on 2026-10-31 (the
        /// player-v1.4.0 entry, trimmed to the fields read; values as published).</summary>
        private const string LiveFeed140 = @"[{""url"":""https://api.github.com/repos/MtndaleRedneck/Zephyr/releases/428641366"",""html_url"":""https://github.com/MtndaleRedneck/Zephyr/releases/tag/player-v1.4.0"",""id"":428641366,""tag_name"":""player-v1.4.0"",""target_commitish"":""0bec11b70f0d66c17236936d5140542548316222"",""name"":""Zephyr PC Player 1.4.0"",""draft"":false,""prerelease"":false,""created_at"":""2026-10-30T03:43:38Z"",""published_at"":""2026-10-30T04:03:08Z"",""assets"":[{""url"":""https://api.github.com/repos/MtndaleRedneck/Zephyr/releases/assets/690865491"",""id"":690865491,""name"":""Zephyr-PC-1.4.0.exe"",""content_type"":""application/octet-stream"",""state"":""uploaded"",""size"":164216642,""digest"":""sha256:bf201c3915b641fede6d4b1244c726fb41e181075e1389c52d7e4cc51873430b"",""created_at"":""2026-10-30T04:03:11Z"",""updated_at"":""2026-10-30T04:03:18Z"",""browser_download_url"":""https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe""}]}]";

        [Fact]
        public void LiveFeedShape_CanonicalAsset_Offered()
        {
            Feed(LiveFeed140);
            var hit = UpdateCheck.LatestNewerThanOrThrow("1.3.0", _base + "/releases");
            Assert.NotNull(hit);
            Assert.Equal("1.4.0", hit!.Value.Version);
            Assert.Equal("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe", hit.Value.ExeUrl);
            Assert.Equal("bf201c3915b641fede6d4b1244c726fb41e181075e1389c52d7e4cc51873430b", hit.Value.Sha256);
            Assert.Equal("up_to_date", UpdateCheck.CheckOutcomeOrThrow("1.4.0", _base + "/releases"));
        }

        [Fact]
        public void LiveFeedShape_HistoricalZephyrDevAsset_Offered()
        {
            Feed(LiveFeed140.Replace("MtndaleRedneck/Zephyr/releases/download", "MtndaleRedneck/Zephyr-Dev/releases/download"));
            var hit = UpdateCheck.LatestNewerThanOrThrow("1.3.0", _base + "/releases");
            Assert.NotNull(hit);
            Assert.Equal("https://github.com/MtndaleRedneck/Zephyr-Dev/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe", hit!.Value.ExeUrl);
        }

        [Theory]
        [InlineData("https://github.com/someoneelse/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")]   // other owner
        [InlineData("https://github.com/mtndaleredneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")] // owner spelled differently
        [InlineData("https://github.com/MtndaleRedneck/ZephyrPlayer/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")] // other repo
        [InlineData("https://github.com/MtndaleRedneck/Zephyr-Devx/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")] // other repo
        [InlineData("https://github.com/MtndaleRedneck/BingeMote/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")]  // other repo
        [InlineData("https://objects.githubusercontent.com/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")] // other host
        [InlineData("https://api.github.com/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")] // other host
        [InlineData("https://github.com.evil.example/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")] // other host
        [InlineData("http://github.com/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")]   // not https
        [InlineData("https://github.com:8443/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe")] // port
        [InlineData("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe?x=1")] // query
        [InlineData("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v1.3.0/Zephyr-PC-1.4.0.exe")]   // other tag
        public void Feed_RejectsOtherOwnerRepoHost(string assetUrl)
        {
            Feed(LiveFeed140.Replace("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v1.4.0/Zephyr-PC-1.4.0.exe", assetUrl));
            Assert.Null(UpdateCheck.LatestNewerThanOrThrow("1.3.0", _base + "/releases"));
            Assert.False(UpdateCheck.IsAcceptedAssetUrl(assetUrl, "player-v1.4.0", "Zephyr-PC-1.4.0.exe"));
        }

        [Fact]
        public void AssetContract_AcceptsCanonicalAndHistoricalOnly()
        {
            Assert.Equal("MtndaleRedneck/Zephyr", UpdateCheck.Repo);
            Assert.Equal("https://api.github.com/repos/MtndaleRedneck/Zephyr/releases?per_page=30", UpdateCheck.Api);
            Assert.Equal("https://github.com/MtndaleRedneck/Zephyr/releases", UpdateCheck.ReleasePage);
            Assert.True(UpdateCheck.IsAcceptedAssetUrl("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe", "player-v2.0.0", "Zephyr-PC-2.0.0.exe"));
            Assert.True(UpdateCheck.IsAcceptedAssetUrl("https://github.com/MtndaleRedneck/Zephyr-Dev/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe", "player-v2.0.0", "Zephyr-PC-2.0.0.exe"));
            Assert.False(UpdateCheck.IsAcceptedAssetUrl("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.1.exe", "player-v2.0.0", "Zephyr-PC-2.0.0.exe"));
            Assert.True(UpdateCheck.IsAcceptedReleaseDownload("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe"));
            Assert.True(UpdateCheck.IsAcceptedReleaseDownload("https://github.com/MtndaleRedneck/Zephyr-Dev/releases/download/player-v1.2.0/Zephyr-PC-1.2.0.exe"));
            foreach (var bad in new[]
            {
                "https://github.com/other/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe",
                "https://github.com/MtndaleRedneck/Other/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe",
                "https://evil.example/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe",
                "https://objects.githubusercontent.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe",
                "http://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe",
                "https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe#x",
                "https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/sub/Zephyr-PC-2.0.0.exe",
                "https://user@github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/Zephyr-PC-2.0.0.exe",
                "https://github.com/MtndaleRedneck/Zephyr/archive/refs/tags/player-v2.0.0.zip",
                "not a url",
            })
                Assert.False(UpdateCheck.IsAcceptedReleaseDownload(bad), bad);
        }

        [Fact]
        public async Task Download_ProductionStart_MustBeTheRepositoryReleaseDownload()
        {
            // No host rule passed = production: other owners, repositories and hosts are refused
            // before any request is made.
            foreach (var bad in new[]
            {
                "https://github.com/other/Zephyr/releases/download/player-v9.0.0/Zephyr-PC-9.0.0.exe",
                "https://github.com/MtndaleRedneck/Other/releases/download/player-v9.0.0/Zephyr-PC-9.0.0.exe",
                "https://objects.githubusercontent.com/x/Zephyr-PC-9.0.0.exe",
            })
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SelfUpdate.Download("9.0.0", bad, new string('c', 64)));
                Assert.Contains("not trusted", ex.Message);
            }
        }
    }
}
