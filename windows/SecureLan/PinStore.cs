using System.Security.Cryptography;
namespace SecureLan;

public enum PinCheck { FirstUseRemembered, Match, Mismatch }

/// <summary>Phone-side per-PC key memory (trust on first use). First successful enrollment stores the full SHA-256 of the server leaf SPKI; every later connection must match, a different key hard-fails and is never silently replaced (explicit local re-pair clears the entry). Pure logic; persistence is injected so storage can be atomic with the token.</summary>
public sealed class PinStore
{
    readonly object _lock = new();
    readonly Dictionary<string, byte[]> _pins = new();   // pcId (stable local id chosen at pairing) -> 32-byte hash

    public static byte[] HashSpki(ReadOnlySpan<byte> spkiDer) => SHA256.HashData(spkiDer);

    /// <summary>Check what the server presented. Unknown PC: returns FirstUseRemembered only when enroll is true (an explicit pairing in progress); otherwise Mismatch (no silent enrollment from discovery).</summary>
    public PinCheck Check(string pcId, ReadOnlySpan<byte> presentedSpkiDer, bool enroll, out byte[] presentedHash)
    {
        presentedHash = HashSpki(presentedSpkiDer);
        lock (_lock)
        {
            if (_pins.TryGetValue(pcId, out var pin))
                return CryptographicOperations.FixedTimeEquals(pin, presentedHash) ? PinCheck.Match : PinCheck.Mismatch;
            return enroll ? PinCheck.FirstUseRemembered : PinCheck.Mismatch;
        }
    }

    /// <summary>Commit the pin. Call only after the PC returned the token, together with the token write (caller makes the pair atomic). Refuses to overwrite a different existing pin.</summary>
    public bool Commit(string pcId, byte[] hash)
    {
        if (hash.Length != 32) return false;
        lock (_lock)
        {
            if (_pins.TryGetValue(pcId, out var pin)) return CryptographicOperations.FixedTimeEquals(pin, hash);
            _pins[pcId] = (byte[])hash.Clone();
            return true;
        }
    }

    /// <summary>Explicit local re-pair: the only way to drop a remembered key.</summary>
    public bool Forget(string pcId) { lock (_lock) return _pins.Remove(pcId); }
}
