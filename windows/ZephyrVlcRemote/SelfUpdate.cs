using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace ZephyrVlcRemote;

/// <summary>
/// Downloads the installer for an available update, checks it against the release's
/// SHA-256, then hands it to Windows. Only GitHub hosts are followed. Nothing installs
/// on its own: the installer still shows and Windows asks for permission once.
/// </summary>
internal static class SelfUpdate
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> Verified = new();
    private static readonly HttpClient Http = new(new HttpClientHandler {AllowAutoRedirect=false});

    internal static void CleanOldDownloads()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZephyrVlcRemote","Updates");
        try{foreach(var dir in Directory.GetDirectories(root))if(Directory.GetLastWriteTimeUtc(dir)<DateTime.UtcNow.AddDays(-1)){try{Directory.Delete(dir,true);}catch{}}}catch{}
    }

    /// <summary>Fetch the installer to the temp folder and verify its SHA-256 when the
    /// release published one. Returns the local path; throws on any failure. A download that
    /// stalls for 60 seconds is canceled so the button never sits on "Downloading..." forever.
    /// Slow connections may take as long as they need: only silence counts.</summary>
    public static async Task<string> Download(string version, string exeUrl, string? sha256, IProgress<int>? progress = null)
    {
        // Round-5: no verified checksum, no execute - an undigested release asset is
        // refused outright instead of being run unverified.
        if (sha256==null || !System.Text.RegularExpressions.Regex.IsMatch(sha256,"^[0-9a-fA-F]{64}$"))
            throw new InvalidOperationException("The release has no SHA-256 digest; the download was not started.");
        return await DownloadCore(version, exeUrl, sha256, progress);
    }

    private static async Task<string> DownloadCore(string version, string exeUrl, string? sha256, IProgress<int>? progress = null)
    {
        using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var current = exeUrl;
        for (var hops = 0; ; hops++)
        {
            var uri=new Uri(current);
            if(uri.Scheme!=Uri.UriSchemeHttps)throw new InvalidOperationException("The download address was not HTTPS.");
            var host = uri.Host;
            if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The download address was not trusted: " + host);
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.UserAgent.ParseAdd("ZephyrVlcRemotePC");
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
            // Fresh random folder: a predictable %TEMP% name can be pre-planted.
            var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZephyrVlcRemote", "Updates", Guid.NewGuid().ToString("N"), "installer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using (var fs = new FileStream(dest,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
                var buf = new byte[64 * 1024];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), stall.Token);
                    got += n;
                    // Every arrived chunk proves the download is alive: the 60 seconds only
                    // ever count silence.
                    stall.CancelAfter(TimeSpan.FromSeconds(60));
                    if (total > 0) progress?.Report((int)(got * 100 / total));
                }
            }
            if (sha256 != null)
            {
                using var sha = SHA256.Create();
                await using var rs = File.OpenRead(dest);
                var hex = Convert.ToHexString(await sha.ComputeHashAsync(rs)).ToLowerInvariant();
                if (!hex.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(dest); } catch { }
                    throw new InvalidOperationException("The download did not match the release's SHA-256.");
                }
            }
            Verified[dest]=sha256!;
            return dest;
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

    /// <summary>Open the downloaded installer (it asks Windows for permission once) and
    /// quit so it can replace the running exe.</summary>
    public static void LaunchAndQuit(string path)
    {
        using var verified=OpenVerifiedForLaunch(path);
        Program.Log("Starting the downloaded installer silently, quitting for the update.");
        // Tell the watchdog an install is about to run so its 5-minute check cannot start a
        // copy mid-install. Fresh for 3 minutes; any normal start deletes it.
        try { File.WriteAllText(Program.UpdatingMarkerPath, DateTime.UtcNow.ToString("O")); } catch { }
        // One Windows permission prompt, a small progress bar, done: no wizard pages,
        // and it keeps the choices from the first install. The installer relaunches the app.
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
        });
        System.Windows.Forms.Application.Exit();
    }
}
