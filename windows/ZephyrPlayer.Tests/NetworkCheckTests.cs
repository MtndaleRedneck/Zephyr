using System;
using Xunit;
using ZephyrPlayer;

namespace ZephyrPlayer.Tests
{
    public class NetworkCheckTests
    {
        [Fact]
        public void DefaultRouteProfileParsesOnWindows()
        {
            if (!OperatingSystem.IsWindows()) return; // the probe is Windows-only
            // The CI runner always has a default route. A null here means the PowerShell
            // command line broke again (round-7: nested double quotes silently disabled
            // the public-Wi-Fi notice).
            var p = NetworkCheck.DefaultRouteProfile();
            Assert.NotNull(p);
            Assert.True(p!.InterfaceIndex >= 0);
            Assert.False(string.IsNullOrEmpty(p.Category));
        }
    }
}
