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
        }

        [Fact]
        public void One_Source_Backs_Off_Alone_And_Another_Source_Still_Pairs()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var code = gate.NewCode();
            for (var i = 0; i < PairingGate.MaxFailures; i++)
                Assert.Equal(PairOutcome.BadCode, gate.TryPair("000000", 0, () => "t", out _, "127.0.0.1"));
            // The attacker is refused, even with the right code, for its own backoff only.
            Assert.Equal(PairOutcome.Locked, gate.TryPair(code, 0, () => "t", out _, "127.0.0.1"));
            // The victim at another address pairs at once, while the attacker is still backed off.
            Assert.Equal(PairOutcome.Ok, gate.TryPair(code, 0, () => "minted", out var token, "127.0.0.2"));
            Assert.Equal("minted", token);
        }

        [Fact]
        public void Source_Backoff_Is_Short_And_Self_Heals()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            for (var round = 0; round < 12; round++)
            {
                gate.NewCode(userStarted: true); // a person reopens the session each round
                for (var i = 0; i < PairingGate.MaxFailures; i++) gate.TryPair("000000", 0, () => "t", out _, "10.0.0.9");
                now += TimeSpan.FromSeconds(31); // longer than the largest backoff step (30 s)
            }
            var code = gate.NewCode(userStarted: true);
            Assert.Equal(PairOutcome.Ok, gate.TryPair(code, 0, () => "minted", out _, "10.0.0.9"));
        }

        [Fact]
        public void Many_Sources_Together_Are_Held_To_The_Session_Budget_And_Pair_Reopens_It()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            var code = gate.NewCode();
            // 10 wrong guesses from 10 DIFFERENT addresses close the session: no address got near its own backoff.
            for (var s = 0; s < PairingGate.SessionGuessBudget; s++)
                Assert.Equal(PairOutcome.BadCode, gate.TryPair("000000", 0, () => "t", out _, "172.16.0." + s));
            Assert.True(gate.Locked);
            Assert.Equal(PairOutcome.Locked, gate.TryPair(code, 0, () => "t", out _, "192.168.1.50"));
            gate.NewCode(); // automatic renewal: stays closed
            Assert.True(gate.Locked);
            var fresh = gate.NewCode(userStarted: true); // a person presses Pair: open at once, no timer
            Assert.False(gate.Locked);
            Assert.Equal(PairOutcome.Ok, gate.TryPair(fresh, 0, () => "minted", out _, "192.168.1.50"));
        }

        [Fact]
        public void Source_Table_Stays_Bounded_Across_Sessions()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            for (var round = 0; round < 400; round++)
            {
                gate.NewCode(userStarted: true);
                for (var s = 0; s < PairingGate.SessionGuessBudget; s++)
                    gate.TryPair("000000", 0, () => "t", out _, "10." + (round / 250) + "." + (round % 250) + "." + s);
            }
            Assert.True(gate.TrackedSources <= 1024);
        }

        [Fact]
        public void Guesses_With_No_Code_Or_An_Expired_Code_Do_Not_Advance_A_Source()
        {
            var now = DateTime.UtcNow;
            var gate = new PairingGate(() => now);
            for (var i = 0; i < 20; i++) Assert.Equal(PairOutcome.NoCode, gate.TryPair("000000", 0, () => "t", out _, "10.1.1.1"));
            var code = gate.NewCode();
            Assert.Equal(PairOutcome.Ok, gate.TryPair(code, 0, () => "minted", out _, "10.1.1.1"));
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

        [Fact]
        public void NewCode_IsFourDigits()
        {
            var gate = new PairingGate();
            for (var i = 0; i < 50; i++) Assert.Matches("^[0-9]{4}$", gate.NewCode());
        }

        [Fact]
        public void SixDigitGuess_AgainstFourDigitCode_IsAStrike()
        {
            var gate = new PairingGate();
            gate.NewCode();
            Assert.Equal(PairOutcome.BadCode, gate.TryPair("123456", 0, () => "t", out _));
        }
    }
}
