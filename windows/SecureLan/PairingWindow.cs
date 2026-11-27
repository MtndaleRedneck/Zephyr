using System.Security.Cryptography;
namespace SecureLan;

public enum SecretKind { Qr, TypedCode }
public enum ConsumeResult { Ok, Rejected, PersistFailed }
public enum WindowState { Idle, Open, Consumed }

/// <summary>One pairing window (spec v3.6 section 3). The typed code does not exist until EnableTypedCode.</summary>
public sealed class PairingWindow
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    readonly Func<DateTimeOffset> _clock;
    readonly object _lock = new();
    WindowState _state = WindowState.Idle;
    byte[]? _qrHash, _codeHash;
    DateTimeOffset _expires;

    public PairingWindow(Func<DateTimeOffset> clock) { _clock = clock; }

    public WindowState State { get { lock (_lock) { ExpireLocked(); return _state; } } }
    public bool CodeEnabled { get { lock (_lock) { ExpireLocked(); return _state == WindowState.Open && _codeHash != null; } } }

    void ExpireLocked() { if (_state == WindowState.Open && _clock() >= _expires) ResetLocked(WindowState.Idle); }
    void ResetLocked(WindowState to) { _qrHash = null; _codeHash = null; _state = to; }
    static byte[] H(ReadOnlySpan<byte> s) => SHA256.HashData(s);

    /// <summary>The user pressed Pair on the PC. Returns the 128-bit QR secret. A new Open replaces any earlier window.</summary>
    public byte[] Open()
    {
        lock (_lock)
        {
            var qr = RandomNumberGenerator.GetBytes(16);
            _qrHash = H(qr); _codeHash = null; _expires = _clock() + Lifetime; _state = WindowState.Open;
            return qr;
        }
    }

    /// <summary>The user chose Can't scan on the PC. Adds the 64-bit code to the SAME window and expiry. Returns null if not Open or already enabled.</summary>
    public byte[]? EnableTypedCode()
    {
        lock (_lock)
        {
            ExpireLocked();
            if (_state != WindowState.Open || _codeHash != null) return null;
            var code = RandomNumberGenerator.GetBytes(8);
            _codeHash = H(code);
            return code;
        }
    }

    public void Cancel() { lock (_lock) ResetLocked(WindowState.Idle); }

    /// <summary>Atomic check, compare, consume and persist under one lock. A wrong guess burns nothing. A persist failure leaves the window closed.</summary>
    public ConsumeResult TryConsume(SecretKind kind, ReadOnlySpan<byte> secret, Func<bool> persist)
    {
        lock (_lock)
        {
            ExpireLocked();
            if (_state != WindowState.Open) return ConsumeResult.Rejected;
            byte[]? want = kind == SecretKind.Qr ? _qrHash : _codeHash;
            var got = H(secret);
            bool match = want != null && CryptographicOperations.FixedTimeEquals(got, want);
            if (!match) return ConsumeResult.Rejected;
            bool ok;
            try { ok = persist(); } catch { ok = false; }
            if (!ok) { ResetLocked(WindowState.Idle); return ConsumeResult.PersistFailed; }
            ResetLocked(WindowState.Consumed);
            return ConsumeResult.Ok;
        }
    }
}
