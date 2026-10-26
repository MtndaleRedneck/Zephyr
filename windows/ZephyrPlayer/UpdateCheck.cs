// Auto-update check against GitHub releases, ported from the BingeMote helper's
// UpdateCheck. The feed is the future PUBLIC home of the v2.0 merge (the private
// dev repo serves no unauthenticated release downloads, and no tokens go in apps),
// so in the private phase every check fails silent = "no update", by design.
// Only talks HTTPS; never downloads or installs by itself.
using System;
using System.Net.Http;
using System.Text.Json;

namespace ZephyrPlayer
{
    /// <summary>Latest background-check outcome, surfaced via /control/status. A failed
    /// check is recorded AS failed (reason kept) - never indistinguishable from a
    /// verified "no update". Nothing here is user-facing; the status endpoint is the
    /// diagnostic surface.</summary>
    internal static class UpdateState
    {
        /// <summary>null = never ran; "checking"; "failed: <reason>"; "up_to_date"; or "available: <version>".</summary>
        public static volatile string? Outcome;
        public static string? AvailableVersion =>
            Outcome != null && Outcome.StartsWith("available: ") ? Outcome.Substring(11) : null;
    }

    internal static class UpdateCheck
    {
        private const string Api = "https://api.github.com/repos/MtndaleRedneck/BingeMote/releases?per_page=30";
        /// <summary>Phone, helper and player release separately; this only looks at player-v* tags.</summary>
        private const string TagPrefix = "player-v";
        private const string AssetPrefix = "ZephyrPlayer";
        public const string ReleasePage = "https://github.com/MtndaleRedneck/BingeMote/releases";

        /// <summary>The published version newer than [current] shipping the installer, else null. Never throws.</summary>
        public static (string Version, string ExeUrl, string? Sha256)? LatestNewerThan(string current)
        {
            try { return LatestNewerThanOrThrow(current); }
            catch { return null; }
        }

        /// <summary>Lets failures reach the caller (manual checks tell failed apart from none).</summary>
        public static (string Version, string ExeUrl, string? Sha256)? LatestNewerThanOrThrow(string current, string? apiOverride = null)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZephyrPlayer");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.Timeout = TimeSpan.FromSeconds(8);
            var json = http.GetStringAsync(apiOverride ?? Api).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var local = Parse(current);
            if (local == null) return null;
            foreach (var root in doc.RootElement.EnumerateArray())
            {
                if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
                var tag = (root.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "";
                if (!tag.StartsWith(TagPrefix)) continue;
                var version = tag[TagPrefix.Length..];
                var remote = Parse(version);
                if (remote == null) continue;
                var newer = false;
                for (var i = 0; i < Math.Max(remote.Length, local.Length); i++)
                {
                    var a = i < remote.Length ? remote[i] : 0;
                    var b = i < local.Length ? local[i] : 0;
                    if (a != b) { newer = a > b; break; }
                }
                if (!newer) continue;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.StartsWith(AssetPrefix) && name.EndsWith(".exe"))
                        {
                            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                            string? sha = null;
                            if (a.TryGetProperty("digest", out var dg))
                            {
                                var dsv = dg.GetString() ?? "";
                                if (dsv.StartsWith("sha256:") && dsv.Length == 71) sha = dsv.Substring(7).ToLowerInvariant();
                            }
                            if (url.Length > 0) return (version, url, sha);
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>"v1.8.0" or "1.8.0.0" as numbers, or null when not a plain dotted version.</summary>
        private static int[]? Parse(string s)
        {
            var t = s.Trim().TrimStart('v', 'V');
            var parts = t.Split('.');
            if (parts.Length is < 1 or > 4) return null;
            var nums = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], out nums[i]) || nums[i] is < 0 or > 9999) return null;
            return nums;
        }
    }
}
