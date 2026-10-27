using System;
using System.Security.Cryptography;

namespace ZephyrPlayer
{
    public enum PairOutcome { Ok, BadCode, CodeExpired, Locked, NoCode, TooManyPhones, StoreUnavailable }

    /// <summary>
    /// Pairing protections, mirroring the inherited BingeMote helper model
    /// (helper/Program.cs + HttpServer.cs): crypto-RNG 6-digit code, 10-minute
    /// lifetime, one-shot on success, constant-time compare, wrong guesses while
    /// NO code is showing never count (a LAN guest must not be able to lock
    /// pairing while the user is not pairing), and the store caps at MaxPhones
    /// paired phones. 1.2.0 settles two teardown findings: an EXPIRED code is
    /// told apart from a WRONG code (the phone can say "use the new code on the
    /// PC screen"), and 5 wrong codes lock pairing for LockDuration instead of
    /// until the player restarts (a lock no longer strands him in a reboot).
    /// </summary>
    public sealed class PairingGate
    {
        public const int MaxFailures = 5;
        public const int MaxPhones = 8;
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

        private readonly object _lock = new();
        private readonly Func<DateTime> _now;
        private string? _code;
        private DateTime _expires = DateTime.MinValue;
        // Renewal grace: when the on-screen code renews (or is re-minted), the code it
        // replaced stays acceptable for 30s - a phone mid-type at the renewal boundary
        // must not eat a wrong-code strike (review reconciliation).
        private string? _prevCode;
        private DateTime _prevExpires = DateTime.MinValue;
        private static readonly TimeSpan PrevCodeGrace = TimeSpan.FromSeconds(30);
        private int _failures;
        private DateTime _lockedUntil = DateTime.MinValue;

        public PairingGate(Func<DateTime>? now = null) { _now = now ?? (() => DateTime.UtcNow); }

        /// <summary>Show a fresh code (invalidates the old one). One active code at a time.</summary>
        public string NewCode()
        {
            lock (_lock)
            {
                // Grace only for a LIVE code: an already-expired code (UI stall,
                // sleep/wake) must not be revived for another 30s (round-3 P6).
                if (_code != null && _now() <= _expires) { _prevCode = _code; _prevExpires = _now() + PrevCodeGrace; }
                _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                _expires = _now() + CodeLifetime;
                return _code;
            }
        }

        /// <summary>The Pairing screen's Cancel link: take the code down without spending it.</summary>
        public void CancelCode()
        {
            lock (_lock) { _code = null; _expires = DateTime.MinValue; _prevCode = null; _prevExpires = DateTime.MinValue; }
        }

        /// <summary>After a successful pair the code is spent; the UI stops showing it.</summary>
        public bool HasActiveCode { get { lock (_lock) return _code != null && _now() <= _expires; } }

        /// <summary>Remaining lifetime of the on-screen code (the countdown display).</summary>
        public TimeSpan? CodeTimeLeft
        {
            get
            {
                lock (_lock)
                {
                    if (_code == null) return null;
                    var left = _expires - _now();
                    return left > TimeSpan.Zero ? left : TimeSpan.Zero;
                }
            }
        }

        /// <summary>Locked while a lockout is still running (time-based; no restart needed).</summary>
        public bool Locked { get { lock (_lock) return _failures >= MaxFailures && _now() < _lockedUntil; } }

        /// <param name="phoneCount">Current paired-phone count (PairedStore) for the cap.</param>
        /// <param name="mint">Called once, inside the lock, only when the code is right.</param>
        public PairOutcome TryPair(string code, int phoneCount, Func<string> mint, out string? token)
        {
            lock (_lock)
            {
                token = null;
                if (_failures >= MaxFailures)
                {
                    if (_now() < _lockedUntil) return PairOutcome.Locked;
                    _failures = 0; // the lock ran out; pairing is open again
                }
                // No code on screen: guesses here must not count toward the lock.
                if (_code == null) return PairOutcome.NoCode;
                // A code WAS shown but died: distinct from a wrong code, and a
                // guess against a dead code never counts toward the lock either.
                if (_now() > _expires)
                {
                    _code = null;
                    _expires = DateTime.MinValue;
                    return PairOutcome.CodeExpired;
                }
                if (phoneCount >= MaxPhones) return PairOutcome.TooManyPhones;
                var given = System.Text.Encoding.UTF8.GetBytes(code);
                var expected = System.Text.Encoding.UTF8.GetBytes(_code);
                var matches = given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
                if (!matches && _prevCode != null && _now() <= _prevExpires)
                {
                    var prev = System.Text.Encoding.UTF8.GetBytes(_prevCode);
                    matches = given.Length == prev.Length && CryptographicOperations.FixedTimeEquals(given, prev);
                }
                if (!matches)
                {
                    _failures++;
                    if (_failures >= MaxFailures) _lockedUntil = _now() + LockDuration;
                    return PairOutcome.BadCode;
                }
                token = mint();
                _code = null; // one-shot
                _expires = DateTime.MinValue;
                _prevCode = null;
                _prevExpires = DateTime.MinValue;
                _failures = 0;
                return PairOutcome.Ok;
            }
        }
    }
}
