using System;
using System.Security.Cryptography;

namespace ZephyrPlayer
{
    public enum PairOutcome { Ok, BadCode, Locked, NoCode, TooManyPhones }

    /// <summary>
    /// Pairing protections, mirroring the inherited BingeMote helper model
    /// (helper/Program.cs + HttpServer.cs): crypto-RNG 6-digit code, 10-minute
    /// lifetime, one-shot on success, constant-time compare, 5 wrong codes lock
    /// pairing until the player restarts, wrong guesses while NO code is showing
    /// never count (a LAN guest must not be able to lock pairing while the user
    /// is not pairing), and the store caps at MaxPhones paired phones.
    /// </summary>
    public sealed class PairingGate
    {
        public const int MaxFailures = 5;
        public const int MaxPhones = 8;
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

        private readonly object _lock = new();
        private readonly Func<DateTime> _now;
        private string? _code;
        private DateTime _expires = DateTime.MinValue;
        private int _failures;

        public PairingGate(Func<DateTime>? now = null) { _now = now ?? (() => DateTime.UtcNow); }

        /// <summary>Show a fresh code (invalidates the old one). One active code at a time.</summary>
        public string NewCode()
        {
            lock (_lock)
            {
                _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                _expires = _now() + CodeLifetime;
                return _code;
            }
        }

        /// <summary>After a successful pair the code is spent; the UI stops showing it.</summary>
        public bool HasActiveCode { get { lock (_lock) return _code != null && _now() <= _expires; } }

        /// <summary>Locked until the player restarts (same reset semantics as the helper).</summary>
        public bool Locked { get { lock (_lock) return _failures >= MaxFailures; } }

        /// <param name="phoneCount">Current paired-phone count (PairedStore) for the cap.</param>
        /// <param name="mint">Called once, inside the lock, only when the code is right.</param>
        public PairOutcome TryPair(string code, int phoneCount, Func<string> mint, out string? token)
        {
            lock (_lock)
            {
                token = null;
                if (_failures >= MaxFailures) return PairOutcome.Locked;
                // No code on screen: guesses here must not count toward the lock.
                if (_code == null || _now() > _expires) return PairOutcome.NoCode;
                if (phoneCount >= MaxPhones) return PairOutcome.TooManyPhones;
                var expected = System.Text.Encoding.UTF8.GetBytes(_code);
                var given = System.Text.Encoding.UTF8.GetBytes(code);
                if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(given, expected))
                {
                    _failures++;
                    return PairOutcome.BadCode;
                }
                token = mint();
                _code = null; // one-shot
                _expires = DateTime.MinValue;
                return PairOutcome.Ok;
            }
        }
    }
}
