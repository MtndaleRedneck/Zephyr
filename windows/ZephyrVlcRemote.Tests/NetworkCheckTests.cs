using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

public class NetworkCheckTests
{
    [Fact]
    public void DefaultRouteProfileParsesOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // the probe is Windows-only
        // The CI runner always has a default route. A null here means the PowerShell
        // command line broke again (round-7: nested double quotes made Windows argument
        // parsing hand PowerShell garbage, silently disabling the public-Wi-Fi notice).
        var p = NetworkCheck.DefaultRouteProfile();
        Assert.NotNull(p);
        Assert.True(p!.InterfaceIndex >= 0);
        Assert.False(string.IsNullOrEmpty(p.Category));
    }

    [Fact]
    public void ProfileLineParsesFromTheRight()
    {
        // A Wi-Fi name may itself contain '|' (round-8 N-9): the last two fields are
        // category and index, the name is everything before them.
        var p = NetworkCheck.ParseProfileLine("My|Odd Wifi|Public|7");
        Assert.NotNull(p);
        Assert.Equal("My|Odd Wifi", p!.Name);
        Assert.Equal("Public", p.Category);
        Assert.Equal(7, p.InterfaceIndex);
    }

    [Fact]
    public void ProfileLineRejectsWrongShape()
    {
        Assert.Null(NetworkCheck.ParseProfileLine("only|two"));
        Assert.Null(NetworkCheck.ParseProfileLine("a|Private|notanumber"));
    }
}
