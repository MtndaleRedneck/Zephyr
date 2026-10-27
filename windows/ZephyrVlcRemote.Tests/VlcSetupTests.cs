using System.Text.Json;
using ZephyrVlcRemote;
using Xunit;

namespace ZephyrVlcRemote.Tests;

[Collection("ProgramState")]
public class VlcSetupTests : IDisposable
{
    // Round-7 N8: never touch the real %APPDATA%\vlc\vlcrc - a crashed run used to
    // leave the backup un-restored. The seam redirects every vlcrc access to a temp dir.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-vlc-" + Guid.NewGuid().ToString("N"));
    private string VlcrcPath => Path.Combine(_dir, "vlcrc");

    public VlcSetupTests()
    {
        Directory.CreateDirectory(_dir);
        VlcSetup.VlcDirOverride = _dir;
    }

    public void Dispose()
    {
        VlcSetup.VlcDirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void WriteVlcrc(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VlcrcPath)!);
        File.WriteAllLines(VlcrcPath, lines);
    }

    [Fact]
    public void UnchangedSettingsAreANoOpNotAPendingWrite()
    {
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=pw-1-long-password");
        var r = VlcSetup.WriteVlcrc("pw-1-long-password");
        using var doc = JsonDocument.Parse(r);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        // A running VLC read exactly this vlcrc at startup, so nothing may be deferred:
        // a false pending would tell the owner a reopen is required when control works now.
        Assert.False(VlcSetup.Pending);
        Assert.Equal("pw-1-long-password", VlcSetup.ReadVlcrcValue("http-password"));
    }

    [Fact]
    public void ReusingThePasswordAlsoEnsuresInterfaceAndBind()
    {
        WriteVlcrc("http-password=pw-1-long-password"); // a vlcrc can carry a password with the interface off
        var r = VlcSetup.WriteVlcrc("pw-1-long-password");
        using var doc = JsonDocument.Parse(r);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("http", VlcSetup.ReadVlcrcValue("extraintf"));
        Assert.Equal("0.0.0.0", VlcSetup.ReadVlcrcValue("http-host"));
        Assert.False(VlcSetup.Pending);
        Assert.True(VlcSetup.Configured);
    }

    [Fact]
    public void PasswordAloneIsNotConfigured()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VlcrcPath)!);
        if (File.Exists(VlcrcPath)) File.Delete(VlcrcPath);
        WriteVlcrc("http-password=pw-1-long-password");
        Assert.False(VlcSetup.Configured); // no interface, no bind: never a green attestation
    }
}
