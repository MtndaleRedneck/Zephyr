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
    
        // 2.0.2 CrashProofingTests, carried with their original vectors.
        [Fact]
        public void Guard_SurvivesOrdinaryHandlerExceptions_NotCorruptingOnes()
        {
            CrashGuard.ResetForTest();
            var now = DateTime.UtcNow;
            Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException("handler bug"), now));
            Assert.True(CrashGuard.ShouldHandle(new System.IO.IOException("disk"), now));
            Assert.False(CrashGuard.ShouldHandle(new OutOfMemoryException(), now));
            Assert.False(CrashGuard.ShouldHandle(new AccessViolationException(), now));
            Assert.False(CrashGuard.ShouldHandle(new InvalidOperationException("wrapped", new OutOfMemoryException()), now));
            CrashGuard.ResetForTest();
        }

        [Fact]
        public void Guard_ABurstOfExceptions_StopsBeingHandled_ThenRecovers()
        {
            CrashGuard.ResetForTest();
            var t0 = DateTime.UtcNow;
            for (var i = 0; i < CrashGuard.BurstLimit; i++)
                Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException(), t0.AddSeconds(i)));
            // A handler throwing again and again (every frame) is not swallowed forever.
            Assert.False(CrashGuard.ShouldHandle(new InvalidOperationException(), t0.AddSeconds(CrashGuard.BurstLimit)));
            // Once the window has passed, a lone exception is handled again.
            Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException(), t0 + CrashGuard.BurstWindow + TimeSpan.FromSeconds(CrashGuard.BurstLimit + 1)));
            CrashGuard.ResetForTest();
        }
}
}
