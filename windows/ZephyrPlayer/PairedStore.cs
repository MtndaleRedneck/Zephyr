using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZephyrPlayer
{
    /// <summary>
    /// Paired-phone token store for the player (separate identity from the
    /// BingeMote helper's store). JSON at %AppData%/ZephyrPlayer/paired.json,
    /// written by the pairing flow (6-digit code, protocol.md). Secure default:
    /// no file = no paired phones = every /play gets 401.
    /// Format: 1.2.0 stores entries {token, name} so the Pairing screen can list
    /// "My phone" with Remove. The 1.1.0 file was a plain JSON string array of
    /// tokens; it still reads (each token becomes "My phone") and is rewritten in
    /// the new shape only when the list actually changes - an upgrade never
    /// touches the file, so pairing survives with nothing to re-pair.
    /// </summary>
    public sealed class PairedStore
    {
        private static readonly string DefaultPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZephyrPlayer", "paired.json");
        private readonly string _path;
        private readonly object _sync = new();
        // Read once, then the in-memory list is authoritative for the session: the HTTP
        // auth path (IsPaired, every request) never races a UI write again, and a sharing
        // violation on the file can never turn into a spurious 401 (review should-fix 2).
        private List<Entry>? _cache;

        public PairedStore() { _path = DefaultPath; }
        internal PairedStore(string path) { _path = path; }

        public sealed record Entry(string Token, string Name);

        /// <summary>Any paired phone at all (drives the pairing surfaces in the UI).</summary>
        public bool HasAny { get { lock (_sync) return EntriesLocked().Count > 0; } }

        /// <summary>Number of paired phones (the gate's MaxPhones cap input).</summary>
        public int Count { get { lock (_sync) return EntriesLocked().Count; } }

        /// <summary>Token + display name for the Pairing screen's "Paired phones" list.</summary>
        public List<Entry> Entries()
        {
            lock (_sync) return EntriesLocked().Select(e => e with { }).ToList();
        }

        /// <summary>Constant-time token compare, same bar as the pairing gate's codes.</summary>
        public bool IsPaired(string token)
        {
            lock (_sync) return EntriesLocked().Any(e => FixedTime(e.Token, token));
        }

        private static bool FixedTime(string a, string b)
        {
            var ab = System.Text.Encoding.UTF8.GetBytes(a);
            var bb = System.Text.Encoding.UTF8.GetBytes(b);
            return ab.Length == bb.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ab, bb);
        }

        /// <summary>Pairing flow only: persist a fresh token alongside any existing ones.</summary>
        public void Add(string token) => Add(token, "My phone");

        public void Add(string token, string name)
        {
            lock (_sync)
            {
                var entries = EntriesLocked();
                if (entries.Any(e => FixedTime(e.Token, token))) return;
                entries.Add(new Entry(token, string.IsNullOrWhiteSpace(name) ? "My phone" : name.Trim()));
                PersistLocked(entries);
            }
        }

        /// <summary>Forget one phone (Pairing screen "Remove"). Its token stops
        /// authenticating immediately; every other pairing is untouched.</summary>
        public void Remove(string token)
        {
            lock (_sync)
            {
                var entries = EntriesLocked();
                entries.RemoveAll(e => FixedTime(e.Token, token));
                PersistLocked(entries);
            }
        }

        private List<Entry> EntriesLocked() => _cache ??= Read();

        /// <summary>Memory first (auth keeps working), then a best-effort atomic write:
        /// temp file + replace, so a crash mid-write can never unpair every phone. A
        /// failed write is logged-and-ignored, never a UI-thread exception.</summary>
        private void PersistLocked(List<Entry> entries)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var stored = entries.Select(e => new StoredEntry(e.Token, e.Name)).ToArray();
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(stored));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("paired store write failed: " + ex.GetType().Name); }
        }

        // ---------- file shape (old string[] + new entry[] both read) ----------

        private sealed record StoredEntry(string token, string? name);

        private List<Entry> Read()
        {
            try
            {
                if (!File.Exists(_path)) return new List<Entry>();
                var text = File.ReadAllText(_path);
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var entries = new List<Entry>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        // 1.1.0 shape: a bare token string.
                        var t = el.GetString();
                        if (!string.IsNullOrEmpty(t)) entries.Add(new Entry(t, "My phone"));
                    }
                    else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var t = el.TryGetProperty("token", out var tp) ? tp.GetString() : null;
                        var n = el.TryGetProperty("name", out var np) ? np.GetString() : null;
                        if (!string.IsNullOrEmpty(t)) entries.Add(new Entry(t, string.IsNullOrWhiteSpace(n) ? "My phone" : n));
                    }
                }
                return entries;
            }
            catch { return new List<Entry>(); } // unreadable store = nothing paired, never fail-open
        }

    }
}
