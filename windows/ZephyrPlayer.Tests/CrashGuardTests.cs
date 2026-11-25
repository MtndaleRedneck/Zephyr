using System;
using Xunit;

namespace ZephyrPlayer.Tests
{
    public class CrashGuardTests
    {
        [Fact]
        public void OrdinaryExceptions_AreSurvived_ButNotInAnEndlessBurst()
        {
            CrashGuard.ResetForTest();
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < CrashGuard.BurstLimit; i++) Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException("x"), now.AddSeconds(i)));
            Assert.False(CrashGuard.ShouldHandle(new InvalidOperationException("x"), now.AddSeconds(10)));   // burst: let it close
            Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException("x"), now.AddMinutes(2)));     // window passed
            CrashGuard.ResetForTest();
        }

        [Fact]
        public void ProcessCorruptingExceptions_AreNeverSurvived()
        {
            CrashGuard.ResetForTest();
            var now = DateTime.UtcNow;
            Assert.False(CrashGuard.ShouldHandle(new OutOfMemoryException(), now));
            Assert.False(CrashGuard.ShouldHandle(new AccessViolationException(), now));
            Assert.False(CrashGuard.ShouldHandle(new InvalidOperationException("wrap", new OutOfMemoryException()), now));
            CrashGuard.ResetForTest();
        }
    }
}
