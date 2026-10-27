using System.Text.Json;

namespace ZephyrVlcRemote;

/// <summary>
/// One-time, read-only import from a BingeMote helper install. The settled outcome is
/// that an existing phone pairing to this PC's VLC helper is preserved where possible:
/// the phone must not be forced to re-pair just because the helper changed product.
/// This runs only when Zephyr VLC Remote has no config of its own yet, only ever READS
/// the legacy file, and imports the pairings (token + name; a spent VLC-password grant
/// stays spent, an in-flight one is dropped) plus the Wi-Fi and movies-folder choices,
/// which belong to this same PC and user. The legacy install keeps working untouched.
/// </summary>
internal static class BingeMigration
{
    internal sealed class Imported
    {
        public List<(string Token, string Name, bool AdoptSpent)> Phones { get; } = new();
        public List<string> DeclinedNetworks { get; } = new();
        public List<string> HomeNetworks { get; } = new();
        public string? MoviesFolder;
    }

    /// <summary>The legacy helper's config, only ever read.</summary>
    public static string DefaultLegacyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BingeMote", "config.json");

    /// <summary>Parses legacy config JSON into an import, or null when there is nothing
    /// worth carrying over. Pure: no IO beyond the text handed in, no side effects.</summary>
    public static Imported? Parse(string legacyJson)
    {
        using var doc = JsonDocument.Parse(legacyJson);
        var root = doc.RootElement;
        var result = new Imported();
        if (root.TryGetProperty("phones", out var ps))
            foreach (var p in ps.EnumerateArray())
            {
                var t = p.TryGetProperty("token", out var pt) ? pt.GetString() : null;
                if (string.IsNullOrEmpty(t)) continue;
                var n = p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                // A spent grant stays spent (the phone already holds this PC's VLC
                // password); an in-flight grant is dropped, never replayed across helpers.
                var spent = p.TryGetProperty("adoptSpent", out var asp) && asp.GetBoolean();
                result.Phones.Add((t, string.IsNullOrWhiteSpace(n) ? "Phone " + (result.Phones.Count + 1) : n, spent));
            }
        // Legacy shapes, same as our own loader understands.
        if (result.Phones.Count == 0 && root.TryGetProperty("tokens", out var ts))
            foreach (var t in ts.EnumerateArray())
                if (t.GetString() is string v && v.Length > 0)
                    result.Phones.Add((v, "Phone " + (result.Phones.Count + 1), false));
        if (result.Phones.Count == 0 && root.TryGetProperty("token", out var old) && old.GetString() is string ov && ov.Length > 0)
            result.Phones.Add((ov, "Phone 1", false));
        if (root.TryGetProperty("declinedNetworks", out var dn))
            foreach (var n in dn.EnumerateArray())
                if (n.GetString() is string v && !string.IsNullOrWhiteSpace(v)) result.DeclinedNetworks.Add(v);
        if (root.TryGetProperty("homeNetworks", out var hn))
            foreach (var n in hn.EnumerateArray())
                if (n.GetString() is string v && !string.IsNullOrWhiteSpace(v)) result.HomeNetworks.Add(v);
        result.MoviesFolder = root.TryGetProperty("moviesFolder", out var mf) ? mf.GetString() : null;
        if (result.Phones.Count == 0 && result.DeclinedNetworks.Count == 0
            && result.HomeNetworks.Count == 0 && result.MoviesFolder == null)
            return null;
        return result;
    }

    /// <summary>Writes the import as OUR config (the same shape SaveConfig writes). The
    /// caller guarantees our config does not exist yet. The source file is never touched.</summary>
    public static void WriteAsOwnConfig(Imported imported, string ownConfigPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ownConfigPath)!);
        var json = JsonSerializer.Serialize(new
        {
            phones = imported.Phones.Select(p => new { token = p.Token, name = p.Name, adoptSpent = p.AdoptSpent }).ToArray(),
            moviesFolder = imported.MoviesFolder,
            declinedNetworks = imported.DeclinedNetworks.ToArray(),
            homeNetworks = imported.HomeNetworks.ToArray(),
        });
        // Atomic: a crash mid-write must leave no half config that suppresses a retry.
        var tmp = ownConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(ownConfigPath)) File.Replace(tmp, ownConfigPath, null);
        else File.Move(tmp, ownConfigPath);
    }

    /// <summary>First-run entry point: import only when we have no config yet. Any failure
    /// means "no migration" - a migration must never block a first run.</summary>
    public static bool TryImport(string legacyPath, string ownConfigPath)
    {
        try
        {
            if (File.Exists(ownConfigPath) || !File.Exists(legacyPath)) return false;
            var parsed = Parse(File.ReadAllText(legacyPath));
            if (parsed == null) return false;
            WriteAsOwnConfig(parsed, ownConfigPath);
            return true;
        }
        catch { return false; }
    }
}
