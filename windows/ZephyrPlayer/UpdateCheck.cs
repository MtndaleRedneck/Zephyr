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
        /// <summary>null = never ran; "checking"; "failed: <reason>"; "up_to_date"; or "available: <version>".</summary>
        public static volatile string? Outcome;
        public sealed record Offer(string Version, string ExeUrl, string? Sha256);
        public static Offer? AvailableOffer;
        public static string Display(string? outcome) => outcome switch
        {
            null => "Not checked yet.",
            "checking" => "Checking...",
            "no_release" => "No published player installer available yet.",
            "up_to_date" => "You have the latest published player version.",
            _ when outcome.StartsWith("available: ") => "Update available: " + outcome.Substring(11),
            _ when outcome.StartsWith("failed:") => "Could not check for updates. Try again later.",
            _ => "Update status unavailable."
        };
        public static string? AvailableVersion =>
            Outcome != null && Outcome.StartsWith("available: ") ? Outcome.Substring(11) : null;
    }

    internal static class UpdateCheck
    {
        private const string Api = "https://api.github.com/repos/MtndaleRedneck/Zephyr-Dev/releases?per_page=30";
        /// <summary>Phone, helper and player release separately; this only looks at player-v* tags.</summary>
        private const string TagPrefix = "player-v";
        private const string AssetPrefix = "ZephyrPlayer";
        public const string ReleasePage = "https://github.com/MtndaleRedneck/Zephyr-Dev/releases";

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
            if (Compare(Parse(published.Value.Version)!, Parse(current) ?? throw new ArgumentException("Invalid installed version")) > 0)
                UpdateState.AvailableOffer = new UpdateState.Offer(published.Value.Version, published.Value.ExeUrl, published.Value.Sha256);
            return Compare(Parse(published.Value.Version)!, Parse(current) ?? throw new ArgumentException("Invalid installed version")) > 0
                ? "available: " + published.Value.Version : "up_to_date";
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
                    var expected = $"https://github.com/MtndaleRedneck/Zephyr-Dev/releases/download/{tag}/{name}";
                    // Loopback feed tests may use a loopback asset; production never can.
                    bool testAsset = apiOverride != null && Uri.TryCreate(apiOverride, UriKind.Absolute, out var apiUri) && apiUri.IsLoopback
                        && Uri.TryCreate(url, UriKind.Absolute, out var assetUri) && assetUri.IsLoopback;
                    if (url != expected && !testAsset) continue;
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
