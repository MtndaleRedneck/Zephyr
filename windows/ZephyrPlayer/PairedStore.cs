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
    /// tokens; it still reads (each token becomes "My phone") and is atomically migrated after a successful read to protected-token entries.
    /// Tokens and names survive the upgrade without re-pairing.
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
        /// <summary>The existing file could not be read at first load. Fail CLOSED:
        /// writes refuse (never replace an unreadable store with a partial one - that
        /// would silently unpair every phone in it).</summary>
        private bool _broken;
        private bool _transientReadFailure;
        private long _nextReadRetry;
        public bool CanResetUnreadable {get {lock(_sync)return _broken&&!_transientReadFailure;}}
        /// <summary>The store file exists but could not be read: reads show unpaired and
        /// writes refuse. Surfaced as a plain line on the Pairing screen (review N5).</summary>
        public bool IsBroken { get { lock (_sync) return _broken; } }

        /// <summary>Raised (under no lock guarantee) after a pairing is added or removed;
        /// open Settings windows refresh their "Paired with N phones" line.</summary>
        public event EventHandler? Changed;

        public PairedStore() { _path = DefaultPath; }
        internal PairedStore(string path) { _path = path; }

        public sealed record Entry(string Token, string Name, DateTime? PairedAt = null);
        private readonly Dictionary<string, DateTime> _lastSeen = new();
        public bool ValidateAndTouch(string token)
        {
            lock (_sync)
            {
                if (!EntriesLocked().Any(e => FixedTime(e.Token, token))) return false;
                _lastSeen[token] = DateTime.UtcNow;
                return true;
            }
        }
        public DateTime? LastSeen(string token) { lock (_sync) return _lastSeen.TryGetValue(token, out var seen) ? seen : null; }
        public bool IsConnected(string token) => LastSeen(token) is DateTime seen && DateTime.UtcNow - seen < TimeSpan.FromSeconds(15);

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
                // Broken store: refuse BEFORE touching memory - a refused write must
                // never leave a phantom entry that IsPaired/Count/the UI then trust
                // (round-3 P4). Disk first too: only a persisted change reaches _cache,
                // so a failed write changes nothing anywhere.
                if (_broken) throw new InvalidOperationException("The saved pairings file could not be read, so nothing was changed.");
                if (entries.Any(e => FixedTime(e.Token, token))) return;
                var next = entries.Select(e => e with { }).ToList();
                next.Add(new Entry(token, string.IsNullOrWhiteSpace(name) ? "My phone" : name.Trim(), DateTime.UtcNow));
                PersistLocked(next);
                _cache = next;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Forget one phone (Pairing screen "Remove"). Its token stops
        /// authenticating immediately; every other pairing is untouched.</summary>
        public void Remove(string token)
        {
            lock (_sync)
            {
                var entries = EntriesLocked();
                if (_broken) throw new InvalidOperationException("The saved pairings file could not be read, so nothing was changed.");
                var next = entries.Where(e => !FixedTime(e.Token, token)).Select(e => e with { }).ToList();
                if (next.Count == entries.Count) return; // nothing to forget
                PersistLocked(next);
                _cache = next;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Only the explicit confirmed recovery UI calls this. Preserve the
        /// unreadable original under a random same-profile name, never auto reset.</summary>
        internal string ResetUnreadablePairings()
        {
            string quarantine;
            lock(_sync)
            {
                _=EntriesLocked();
                if(!_broken||_transientReadFailure)throw new InvalidOperationException("Pairings are readable or temporarily unavailable; recovery reset is not available.");
                quarantine=_path+".quarantine-"+Guid.NewGuid().ToString("N");
                File.Move(_path,quarantine);
                try{_broken=false;PersistLocked(new List<Entry>());_cache=new List<Entry>();_lastSeen.Clear();}
                catch{_broken=true;try{if(File.Exists(_path))File.Delete(_path);File.Move(quarantine,_path);}catch{}throw;}
            }
            Changed?.Invoke(this,EventArgs.Empty);
            return quarantine;
        }

        private List<Entry> EntriesLocked()
        {
            if(_transientReadFailure && Environment.TickCount64>=_nextReadRetry){_transientReadFailure=false;_broken=false;_cache=null;}
            return _cache??=Read();
        }

        /// <summary>Transactional: memory advances ONLY after the write lands. A
        /// failed write throws a plain InvalidOperationException, so a phantom
        /// pairing can never exist and no entry is half-saved.</summary>
        private void PersistLocked(List<Entry> entries)
        {
            if (_broken) throw new InvalidOperationException("paired store unreadable; refusing to overwrite it");
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var stored = entries.Select(e => new StoredEntry(Dpapi.Wrap(e.Token), e.Name, e.PairedAt)).ToArray();
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(stored));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                // A failed write is NOT swallowed (round-2/round-3 ask): the caller
                // gets a plain error and the in-memory list stays unchanged.
                System.Diagnostics.Debug.WriteLine("paired store write failed: " + ex.GetType().Name);
                throw new InvalidOperationException("The pairing could not be saved. Nothing was changed.", ex);
            }
        }

        // ---------- file shape (old string[] + new entry[] both read) ----------

        private sealed record StoredEntry(string token, string? name, DateTime? pairedAt);

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
                        var t = Dpapi.Unwrap(el.GetString() ?? "") ?? throw new InvalidDataException("Pairing protection could not be opened");
                        if (!string.IsNullOrEmpty(t)) entries.Add(new Entry(t, "My phone"));
                    }
                    else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var t = Dpapi.Unwrap(el.TryGetProperty("token", out var tp) ? tp.GetString() ?? "" : "") ?? throw new InvalidDataException("Pairing protection could not be opened");
                        var n = el.TryGetProperty("name", out var np) ? np.GetString() : null;
                        DateTime? pairedAt = el.TryGetProperty("pairedAt", out var pp) && pp.ValueKind == System.Text.Json.JsonValueKind.String && pp.TryGetDateTime(out var pd) ? pd : null;
                        if (!string.IsNullOrEmpty(t)) entries.Add(new Entry(t, string.IsNullOrWhiteSpace(n) ? "My phone" : n, pairedAt));
                    }
                }
                if (entries.Count > 0 && doc.RootElement.EnumerateArray().Any(el => el.ValueKind == System.Text.Json.JsonValueKind.String || (el.TryGetProperty("token", out var token) && !(token.GetString() ?? "").StartsWith("dpapi:", StringComparison.Ordinal))))
                {
                    try { PersistLocked(entries); }
                    catch(InvalidOperationException) { /* Valid read remains authoritative; next ordinary write retries protection. */ }
                }
                return entries;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                _broken=true;_transientReadFailure=true;_nextReadRetry=Environment.TickCount64+3000;
                return new List<Entry>();
            }
            catch { _broken = true; return new List<Entry>(); } // unreadable store = nothing paired, never fail-open; writes refuse below
        }

    }
}
