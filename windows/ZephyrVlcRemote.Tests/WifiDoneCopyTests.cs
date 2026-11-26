using Xunit;

namespace ZephyrVlcRemote.Tests;

public class WifiDoneCopyTests
{
    [Fact]
    public void SavedChoice_SaysWindowsRemembersIt()
    {
        var t = HelperWindow.PrivateDoneText(true);
        Assert.Contains("Windows remembers it", t);
        Assert.DoesNotContain("ask about this Wi-Fi again", t);
    }

    [Fact]
    public void UnsavedChoice_NeverClaimsItIsRemembered_AndWarnsItMayAskAgain()
    {
        var t = HelperWindow.PrivateDoneText(false);
        Assert.DoesNotContain("remembers it", t);
        Assert.Contains("may ask about this Wi-Fi again", t);
        Assert.Contains("Private", t);
    }
}
