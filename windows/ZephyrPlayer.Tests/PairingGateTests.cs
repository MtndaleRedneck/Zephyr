using System;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// The 1.2.0 gate semantics straight against the clock: expired vs wrong
    /// codes are told apart, the lock is time-based, CancelCode works.
    /// </summary>
    public class PairingGateTests
    {
        [Fact]
        public void Expired_Code_Is_Not_A_Wrong_Code()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var code = gate.NewCode();
            now += PairingGate.CodeLifetime + TimeSpan.FromSeconds(1);
            Assert.Equal(PairOutcome.CodeExpired, gate.TryPair(code, 0, () => "t", out _));
            // The dead code is consumed: further guesses see "no code showing".
            Assert.Equal(PairOutcome.NoCode, gate.TryPair(code, 0, () => "t", out _));
        }

        [Fact]
        public void Guesses_Against_An_Expired_Code_Never_Lock()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var code = gate.NewCode();
            now += PairingGate.CodeLifetime + TimeSpan.FromSeconds(1);
            for (var i = 0; i < PairingGate.MaxFailures + 3; i++)
            {
                var outcome = gate.TryPair(code, 0, () => "t", out _);
                Assert.True(outcome is PairOutcome.CodeExpired or PairOutcome.NoCode);
            }
            Assert.False(gate.Locked);
        }

        [Fact]
        public void Renewal_Keeps_Previous_Code_Alive_For_A_Short_Grace()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var first = gate.NewCode();
            var second = gate.NewCode(); // the renewal re-mint
            Assert.NotEqual(first, second);
            // A code typed from just before the flip still pairs - no wrong-guess strike.
            Assert.Equal(PairOutcome.Ok, gate.TryPair(first, 1, () => "t", out _));
        }

        [Fact]
        public void Renewal_Never_Revives_An_Expired_Code()
        {
            // Round-3 P6: a code that already expired (UI stall, sleep/wake) must
            // not be promoted into the 30s grace slot by the renewal.
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var first = gate.NewCode();
            now += TimeSpan.FromMinutes(11); // past the 10-minute lifetime
            gate.NewCode();                  // the stall ends, renewal re-mints
            Assert.Equal(PairOutcome.BadCode, gate.TryPair(first, 1, () => "t", out _));
        }

        [Fact]
        public void Previous_Code_Dies_After_The_Grace()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var first = gate.NewCode();
            gate.NewCode();
            now += TimeSpan.FromSeconds(31);
            Assert.Equal(PairOutcome.BadCode, gate.TryPair(first, 1, () => "t", out _));
            Assert.False(gate.Locked); // one wrong guess counts, but a single one never locks
        }

        [Fact]
        public void Lock_Runs_Out_After_LockDuration()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var code = gate.NewCode();
            for (var i = 0; i < PairingGate.MaxFailures; i++)
                Assert.Equal(PairOutcome.BadCode, gate.TryPair("000000", 0, () => "t", out _));
            Assert.True(gate.Locked);
            Assert.Equal(PairOutcome.Locked, gate.TryPair(code, 0, () => "t", out _));
            // One second before the lock ends: still locked. After: the RIGHT code pairs.
            now += PairingGate.LockDuration - TimeSpan.FromSeconds(1);
            Assert.Equal(PairOutcome.Locked, gate.TryPair(code, 0, () => "t", out _));
            now += TimeSpan.FromSeconds(2);
            Assert.Equal(PairOutcome.Ok, gate.TryPair(code, 0, () => "minted", out var token));
            Assert.Equal("minted", token);
        }

        [Fact]
        public void CancelCode_Takes_The_Code_Down_Without_Spending_It()
        {
            var gate = new PairingGate();
            var code = gate.NewCode();
            Assert.True(gate.HasActiveCode);
            gate.CancelCode();
            Assert.False(gate.HasActiveCode);
            Assert.Equal(PairOutcome.NoCode, gate.TryPair(code, 0, () => "t", out _));
        }

        [Fact]
        public void CodeTimeLeft_Counts_Down()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            gate.NewCode();
            Assert.Equal(PairingGate.CodeLifetime, gate.CodeTimeLeft);
            now += TimeSpan.FromMinutes(1);
            Assert.Equal(PairingGate.CodeLifetime - TimeSpan.FromMinutes(1), gate.CodeTimeLeft);
        }
    }
}
