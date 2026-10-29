// Downloads an update's installer, verifies the release SHA-256, hands it to
// Windows. Ported from the BingeMote helper's SelfUpdate. Only GitHub hosts are
// followed (test seam overrides the host check for loopback). Nothing installs
// on its own: the installer still shows and Windows asks for permission once.
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ZephyrPlayer
{
    internal static class SelfUpdate
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Verified = new();
        private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
        /// <summary>Hard cap on one installer download: a broken or hostile feed
        /// must never fill the disk. Margin evidence: the 1.4.0 Zephyr Player installer
        /// is 164,212,234 bytes (~157 MiB, CI artifact 12934174344); the cap is
        /// ~3.4x that, so growth headroom is real but bounded.</summary>
        internal static long MaxInstallerBytes = 512L * 1024 * 1024;

        internal static void CleanOldDownloads()
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZephyrPlayer","Updates");
            try { foreach(var dir in Directory.GetDirectories(root)) if(Directory.GetLastWriteTimeUtc(dir)<DateTime.UtcNow.AddDays(-1)) {try {Directory.Delete(dir,true);}catch{}} } catch {}
        }

        /// <summary>Fetch the installer to temp and verify its SHA-256 when published.
        /// Returns the local path; throws on any failure. A 60s silence cancels;
        /// slow connections take as long as they need.</summary>
        public static async Task<string> Download(string version, string exeUrl, string? sha256,
            IProgress<int>? progress = null, Func<string, bool>? hostAllowed = null)
        {
            if (sha256 == null || !System.Text.RegularExpressions.Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$"))
                throw new InvalidOperationException("The release has no valid SHA-256. Nothing was downloaded.");
            hostAllowed ??= host =>
                host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var current = exeUrl;
            for (var hops = 0; ; hops++)
            {
                var uri = new Uri(current);
                var host = uri.Host;
                if ((!uri.IsLoopback && uri.Scheme != Uri.UriSchemeHttps) || !hostAllowed(host))
                    throw new InvalidOperationException("The download address was not trusted: " + host);
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.UserAgent.ParseAdd("ZephyrPlayer");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                var code = (int)resp.StatusCode;
                if (code is >= 300 and < 400)
                {
                    var loc = resp.Headers.Location ?? throw new InvalidOperationException("The download could not start.");
                    current = new Uri(new Uri(current), loc).ToString();
                    if (hops >= 5) throw new InvalidOperationException("The download redirected too many times.");
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZephyrPlayer", "Updates", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, "installer.exe");
                var total = resp.Content.Headers.ContentLength ?? -1;
                try
                {
                if (total > MaxInstallerBytes) throw new InvalidOperationException("The update download is too large to be a real installer.");
                await using (var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
                    var buf = new byte[64 * 1024];
                    long got = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), stall.Token);
                        got += n;
                        if (got > MaxInstallerBytes) throw new InvalidOperationException("The update download is too large to be a real installer.");
                        stall.CancelAfter(TimeSpan.FromSeconds(60));
                        if (total > 0) progress?.Report((int)(got * 100 / total));
                    }
                }
                {
                    string hex;
                    using (var sha = SHA256.Create())
                    {
                        // Dispose BEFORE the delete below: on Windows an open
                        // handle makes File.Delete fail (CI-observed leftover).
                        await using var rs = File.OpenRead(dest);
                        hex = Convert.ToHexString(await sha.ComputeHashAsync(rs)).ToLowerInvariant();
                    }
                    if (!hex.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(dest); } catch { }
                        throw new InvalidOperationException("The download did not match the release's SHA-256.");
                    }
                }
                Verified[dest] = sha256!;
                return dest;
                }
                catch { try { Directory.Delete(dir, true); } catch { } throw; }
            }
        }

        internal static FileStream OpenVerifiedForLaunch(string path)
        {
            if (!Verified.TryGetValue(path, out var expected)) throw new InvalidOperationException("Download was not verified in this session.");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The installer changed after download. It was not started.");
                stream.Position = 0;
                return stream;
            }
            catch { stream.Dispose(); Verified.TryRemove(path, out _); try { File.Delete(path); } catch { } throw; }
        }

        /// <summary>Open the downloaded installer (one Windows permission prompt) and
        /// quit so it can replace the running exe. The installer relaunches the app.</summary>
        public static void LaunchAndQuit(string path)
        {
            // Keep a read-only, non-delete-sharing handle through process creation.
            // Rehash the same file immediately before launch, and prevent replacement.
            using var verified = OpenVerifiedForLaunch(path);
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            });
            System.Windows.Application.Current?.Shutdown();
        }
    }
}
