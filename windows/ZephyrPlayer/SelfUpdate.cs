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
        private static readonly HttpClient Http = new();

        /// <summary>Fetch the installer to temp and verify its SHA-256 when published.
        /// Returns the local path; throws on any failure. A 60s silence cancels;
        /// slow connections take as long as they need.</summary>
        public static async Task<string> Download(string version, string exeUrl, string? sha256,
            IProgress<int>? progress = null, Func<string, bool>? hostAllowed = null)
        {
            hostAllowed ??= host =>
                host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var current = exeUrl;
            for (var hops = 0; ; hops++)
            {
                var host = new Uri(current).Host;
                if (!hostAllowed(host))
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
                var dest = Path.Combine(Path.GetTempPath(), "ZephyrPlayer-" + version + ".exe");
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using (var fs = File.Create(dest))
                {
                    await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
                    var buf = new byte[64 * 1024];
                    long got = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), stall.Token);
                        got += n;
                        stall.CancelAfter(TimeSpan.FromSeconds(60));
                        if (total > 0) progress?.Report((int)(got * 100 / total));
                    }
                }
                if (sha256 != null)
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
                return dest;
            }
        }

        /// <summary>Open the downloaded installer (one Windows permission prompt) and
        /// quit so it can replace the running exe. The installer relaunches the app.</summary>
        public static void LaunchAndQuit(string path)
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            });
            System.Windows.Application.Current?.Shutdown();
        }
    }
}
