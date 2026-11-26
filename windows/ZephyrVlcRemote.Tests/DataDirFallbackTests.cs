using Xunit;

namespace ZephyrVlcRemote.Tests;

public class DataDirFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zvr-dd-" + Guid.NewGuid().ToString("N"));
    public DataDirFallbackTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void ChooseDataDir_FailedFinalCheck_ReturnsTheUntouchedTrustedDefault()
    {
        Assert.Equal("D", FirewallSetup.ChooseDataDir("D", "S", _ => false));
        Assert.Equal("D", FirewallSetup.ChooseDataDir("D", null, _ => true));
        Assert.Equal("D", FirewallSetup.ChooseDataDir("D", "  ", _ => true));
        Assert.Equal("S", FirewallSetup.ChooseDataDir("D", "S", _ => true));
    }

    [Fact]
    public void ChooseDataDir_DirectoryThatBecomesALinkBetweenChecks_IsRejected()
    {
        var profile = Path.Combine(_root, "profile"); var data = Path.Combine(profile, "data");
        Directory.CreateDirectory(data);
        Assert.False(FirewallSetup.HasReparsePoint(profile, data));
        var other = Path.Combine(_root, "elsewhere"); Directory.CreateDirectory(other);
        Directory.Delete(data);
        if (!TryLink(data, other)) return; // no privilege to create a link on this machine
        Assert.True(FirewallSetup.HasReparsePoint(profile, data));
        Assert.Equal("D", FirewallSetup.ChooseDataDir("D", data, d => FirewallSetup.IsPathInside(profile, d) && !FirewallSetup.HasReparsePoint(profile, d)));
    }

    [Fact]
    public void ProfileRootThatIsALink_IsRejected()
    {
        var real = Path.Combine(_root, "real"); Directory.CreateDirectory(Path.Combine(real, "data"));
        var profile = Path.Combine(_root, "profileLink");
        if (!TryLink(profile, real)) return;
        Assert.True(FirewallSetup.HasReparsePoint(profile, Path.Combine(profile, "data")));
    }

    private static bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return Directory.Exists(link); }
        catch { return false; }
    }
}
