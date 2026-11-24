using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace ZephyrPlayer.Tests
{
    public class SleepGuardTests
    {
        [Theory]
        [InlineData(true, true, false, true)]
        [InlineData(false, true, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, true, false)]
        public void Rule(bool setting, bool media, bool minimized, bool expected) =>
            Assert.Equal(expected, SleepGuard.ShouldHold(setting, media, minimized));

        [Fact]
        public void HoldsSystemRequiredOnly_NeverDisplay_AndReleasesToContinuousOnly()
        {
            var calls = new List<uint>();
            var g = new SleepGuard(f => { calls.Add(f); return 1; });
            g.Update(true); g.Update(true); g.Update(false); g.Update(false);
            Assert.Equal(new uint[] { SleepGuard.ES_CONTINUOUS | SleepGuard.ES_SYSTEM_REQUIRED, SleepGuard.ES_CONTINUOUS }, calls);
            Assert.Equal(0u, calls[0] & 0x00000002); // ES_DISPLAY_REQUIRED never set
        }

        [Fact]
        public void FailedCall_IsRetriedOnNextUpdate()
        {
            var n = 0;
            var g = new SleepGuard(f => ++n == 1 ? 0u : 1u);
            g.Update(true); Assert.False(g.Held);
            g.Update(true); Assert.True(g.Held);
        }

        [Fact]
        public void OtherThread_CannotReleaseWhatTheOwnerHolds()
        {
            var calls = 0;
            var g = new SleepGuard(f => { calls++; return 1; });
            g.Update(true);
            var t = new Thread(() => g.Update(false)); t.Start(); t.Join();
            Assert.True(g.Held); Assert.Equal(1, calls);
            g.Update(false); Assert.False(g.Held);
        }
    }
}
