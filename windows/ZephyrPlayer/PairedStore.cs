using System;
using System.IO;
using System.Linq;

namespace ZephyrPlayer
{
    /// <summary>
    /// Paired-phone token store for the player (separate identity from the
    /// BingeMote helper's store). JSON string array at
    /// %AppData%/ZephyrPlayer/paired.json, written by the pairing flow (6-digit
    /// code, protocol.md). Secure default: no file = no paired phones = every
    /// /play gets 401.
    /// </summary>
    public sealed class PairedStore
    {
        private static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZephyrPlayer", "paired.json");

        /// <summary>Any paired phone at all (drives the pair banner in the UI).</summary>
        public bool HasAny
        {
            get
            {
                try
                {
                    if (!File.Exists(Path)) return false;
                    var tokens = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path));
                    return tokens != null && tokens.Length > 0;
                }
                catch { return false; }
            }
        }

        /// <summary>Number of paired phones (the gate's MaxPhones cap input).</summary>
        public int Count
        {
            get
            {
                try
                {
                    if (!File.Exists(Path)) return 0;
                    return System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path))?.Length ?? 0;
                }
                catch { return 0; }
            }
        }

        /// <summary>Pairing flow only: persist a fresh token alongside any existing ones.</summary>
        public void Add(string token)
        {
            var tokens = new System.Collections.Generic.List<string>();
            try
            {
                if (File.Exists(Path))
                    tokens.AddRange(System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path)) ?? Array.Empty<string>());
            }
            catch { /* unreadable old store: start clean, never block pairing */ }
            if (!tokens.Contains(token)) tokens.Add(token);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, System.Text.Json.JsonSerializer.Serialize(tokens.ToArray()));
        }

        public bool IsPaired(string token)
        {
            try
            {
                if (!File.Exists(Path)) return false;
                var tokens = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path));
                return tokens != null && tokens.Contains(token);
            }
            catch { return false; } // unreadable store = nothing paired, never fail-open
        }
    }
}
