using System.Security.Cryptography;
using System.Text;
namespace SecureLan;

/// <summary>Hash-only token store. The namespace is mixed into the hash, so a token valid in one store is never valid in another even if the strings match.</summary>
public sealed class TokenStore
{
    readonly string _ns;
    readonly object _lock = new();
    readonly Dictionary<string, string> _byHash = new();   // hash -> id
    readonly Dictionary<string, string> _labels = new();   // id -> label (display only, never authenticates)

    TokenStore(string ns) { _ns = ns; }
    public static TokenStore CreateSecureTls() => new("secure-tls-v1");
    public static TokenStore CreateLegacyPlaintext() => new("legacy-plaintext-v0");

    string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_ns + ":" + token)));

    /// <summary>Issue a fresh 256-bit token. Only the secure TLS store issues; legacy has none (no new plaintext pairing).</summary>
    public (string id, string token) Issue(string label)
    {
        if (_ns != "secure-tls-v1") throw new InvalidOperationException("legacy store never issues");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        lock (_lock) { _byHash[Hash(token)] = id; _labels[id] = label; }
        return (id, token);
    }

    /// <summary>Load a token that already exists (legacy migration list). Only the legacy store allows it.</summary>
    public string AddExisting(string token, string label)
    {
        if (_ns != "legacy-plaintext-v0") throw new InvalidOperationException("only legacy loads existing tokens");
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        lock (_lock) { _byHash[Hash(token)] = id; _labels[id] = label; }
        return id;
    }

    public bool Verify(string? token, out string id)
    {
        id = "";
        if (string.IsNullOrEmpty(token) || token.Length > 512) return false;
        lock (_lock) return _byHash.TryGetValue(Hash(token), out id!);
    }

    /// <summary>Explicit local revoke by id (chosen from the list). Never by label.</summary>
    public bool Revoke(string id)
    {
        lock (_lock)
        {
            string? h = _byHash.FirstOrDefault(kv => kv.Value == id).Key;
            if (h == null) return false;
            _byHash.Remove(h); _labels.Remove(id);
            return true;
        }
    }

    public IReadOnlyList<(string id, string label)> List()
    {
        lock (_lock) return _labels.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
