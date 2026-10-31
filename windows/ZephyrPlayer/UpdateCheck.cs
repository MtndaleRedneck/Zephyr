// Checks only the public Zephyr Player release feed. No tokens in the app.
// Missing releases and failed checks are distinct from verified current.
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
        /// <summary>null = never ran; "checking"; "failed: <reason>"; "no_release"; "up_to_date" (installed == newest published);
        /// "ahead_of_feed" (installed newer than anything published - no published-version claim); or "available: <version>".</summary>
        public static volatile string? Outcome;
        public sealed record Offer(string Version, string ExeUrl, string? Sha256);
        public static Offer? AvailableOffer;
        public static string Display(string? outcome) => outcome switch
        {
            null => "Not checked yet.",
            "checking" => "Checking...",
            "no_release" => "No published player installer available yet.",
            "up_to_date" => "You're up to date.",
            "ahead_of_feed" => "No newer version is available.",
            "unverified_release" => "This update has no verified checksum. It was not offered for installation.",
            _ when outcome.StartsWith("available: ") => "Update available: " + outcome.Substring(11),
            _ when outcome.StartsWith("failed:") => "Could not check for updates. Try again later.",
            _ => "Update status unavailable."
        };
        public static string? AvailableVersion =>
            Outcome != null && Outcome.StartsWith("available: ") ? Outcome.Substring(11) : null;
    }

    internal static class UpdateCheck
    {
        /// <summary>The update feed's repository: MtndaleRedneck/Zephyr. Older 1.4
        /// installs may keep checking the old repository name; the owner installs 2.0
        /// by hand.</summary>
        internal const string Repo = "MtndaleRedneck/Zephyr";
        internal const string Api = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";
        /// <summary>Phone, helper and player release separately; this only looks at player-v* tags.</summary>
        private const string TagPrefix = "player-v";
        private const string AssetPrefix = "Zephyr-PC";
        public const string ReleasePage = "https://github.com/" + Repo + "/releases";

        /// <summary>The canonical asset URL: the canonical repo's own release download for
        /// this exact tag and file name.</summary>
        internal static string ExpectedAssetUrl(string tag, string name) => $"https://github.com/{Repo}/releases/download/{tag}/{name}";

        /// <summary>The repository's name before the 2026-10-30 rename (MtndaleRedneck/Zephyr-Dev).
        /// GitHub redirects it; a feed or asset that still names it is the same repository.</summary>
        internal const string HistoricalRepo = "MtndaleRedneck/Zephyr-Dev";
        /// <summary>Same owner, these two repository names only (CP4 item 94).</summary>
        internal static readonly string[] AcceptedRepos = { Repo, HistoricalRepo };

        /// <summary>CP4 item 94: an asset URL is accepted only as
        /// https://github.com/MtndaleRedneck/Zephyr/releases/download/{tag}/{name} or the same
        /// path under the historical /Zephyr-Dev/ name - exact owner, exact repository, exact
        /// host (github.com, the host rule of SelfUpdate), https, this tag and this file. Any
        /// other owner, repository, host, scheme, port, query or extra path is refused.</summary>
        internal static bool IsAcceptedAssetUrl(string url, string tag, string name)
        {
            foreach (var repo in AcceptedRepos)
                if (string.Equals(url, $"https://github.com/{repo}/releases/download/{tag}/{name}", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The start of a production download (SelfUpdate): any release download of
        /// the accepted repositories, https on github.com, no query, no fragment, no port.</summary>
        internal static bool IsAcceptedReleaseDownload(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length > 0) return false;
            if (!string.Equals(u.Host, "github.com", StringComparison.Ordinal)) return false;
            if (u.Query.Length > 0 || u.Fragment.Length > 0) return false;
            var parts = u.AbsolutePath.Split('/');
            // "", owner, repo, "releases", "download", tag, file
            if (parts.Length != 7 || parts[0].Length != 0) return false;
            if (parts[3] != "releases" || parts[4] != "download" || parts[5].Length == 0 || parts[6].Length == 0) return false;
            var repo = parts[1] + "/" + parts[2];
            return Array.Exists(AcceptedRepos, r => string.Equals(r, repo, StringComparison.Ordinal))
                && string.Equals(url, u.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);
        }

        /// <summary>The published version newer than [current] shipping the installer, else null. Never throws.</summary>
        public static (string Version, string ExeUrl, string? Sha256)? LatestNewerThan(string current)
        {
            try { return LatestNewerThanOrThrow(current); }
            catch { return null; }
        }

        /// <summary>Failure propagates; no published compatible asset is not "current".</summary>
        public static string CheckOutcomeOrThrow(string current, string? apiOverride = null)
        {
            UpdateState.AvailableOffer = null;
            var published = LatestPublishedOrThrow(apiOverride);
            if (published == null) return "no_release";
            var cmp = Compare(Parse(published.Value.Version)!, Parse(current) ?? throw new ArgumentException("Invalid installed version"));
            if (cmp > 0)
            {
                if (published.Value.Sha256 == null) return "unverified_release";
                UpdateState.AvailableOffer = new UpdateState.Offer(published.Value.Version, published.Value.ExeUrl, published.Value.Sha256);
                return "available: " + published.Value.Version;
            }
            // Equal = verified current. Installed AHEAD of the feed (an unpublished build) is not
            // "latest published" - say nothing about published state beyond "no newer version".
            return cmp == 0 ? "up_to_date" : "ahead_of_feed";
        }

        public static (string Version, string ExeUrl, string? Sha256)? LatestNewerThanOrThrow(string current, string? apiOverride = null)
        {
            var published = LatestPublishedOrThrow(apiOverride);
            var local = Parse(current) ?? throw new ArgumentException("Invalid installed version");
            return published != null && Compare(Parse(published.Value.Version)!, local) > 0 ? published : null;
        }

        private static int Compare(int[] a, int[] b)
        {
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var diff = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
                if (diff != 0) return diff;
            }
            return 0;
        }

        private static (string Version, string ExeUrl, string? Sha256)? LatestPublishedOrThrow(string? apiOverride)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZephyrPlayer");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.Timeout = TimeSpan.FromSeconds(8);
            var json = http.GetStringAsync(apiOverride ?? Api).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            (string Version, string ExeUrl, string? Sha256)? best = null;
            foreach (var root in doc.RootElement.EnumerateArray())
            {
                if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
                var tag = (root.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "";
                if (!tag.StartsWith(TagPrefix)) continue;
                var version = tag[TagPrefix.Length..];
                var remote = Parse(version);
                if (remote == null) continue;
                if (!root.TryGetProperty("assets", out var assets)) continue;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name != $"{AssetPrefix}-{version}.exe") continue;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    // Loopback feed tests may use a loopback asset; production never can.
                    bool testAsset = apiOverride != null && Uri.TryCreate(apiOverride, UriKind.Absolute, out var apiUri) && apiUri.IsLoopback
                        && Uri.TryCreate(url, UriKind.Absolute, out var assetUri) && assetUri.IsLoopback;
                    if (!IsAcceptedAssetUrl(url, tag, name) && !testAsset) continue;
                    string? sha = null;
                    if (a.TryGetProperty("digest", out var dg))
                    {
                        var value = dg.GetString() ?? "";
                        if (System.Text.RegularExpressions.Regex.IsMatch(value, "^sha256:[0-9a-fA-F]{64}$")) sha = value.Substring(7).ToLowerInvariant();
                    }
                    if (best == null || Compare(remote, Parse(best.Value.Version)!) > 0) best = (version, url, sha);
                }
            }
            return best;
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
