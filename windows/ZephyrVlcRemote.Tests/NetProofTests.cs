using System.Diagnostics;
using ZephyrVlcRemote;
using Xunit;
using Xunit.Abstractions;

namespace ZephyrVlcRemote.Tests;

/// <summary>CI-only (BM_NET_PROOF=1, disposable runner): production probe + MarkPrivate on the runner's own adapter. Not Wi-Fi.</summary>
public class NetProofTests
{
    private readonly ITestOutputHelper _o;
    public NetProofTests(ITestOutputHelper o) { _o = o; }
    private static string Ps(string s)
    {
        var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"" + s + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!; p.WaitForExit(30000);
        return (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
    }
    [Fact]
    public void ProbeAndMarkPrivateOnDisposableRunner()
    {
        if (Environment.GetEnvironmentVariable("BM_NET_PROOF") != "1") return;
        var start = NetworkCheck.DefaultRouteProfile();
        Assert.NotNull(start);
        _o.WriteLine($"VLCREMOTE START name='{start!.Name}' category={start.Category} ifIndex={start.InterfaceIndex}");
        var original = start.Category;
        try
        {
            Ps("Set-NetConnectionProfile -InterfaceIndex " + start.InterfaceIndex + " -NetworkCategory Public");
            var pub = NetworkCheck.DefaultRouteProfile();
            Assert.NotNull(pub);
            _o.WriteLine($"VLCREMOTE after setting Public: probe category={pub!.Category}");
            Assert.Equal("Public", pub.Category, ignoreCase: true);
            var ok = NetworkCheck.MarkPrivate(pub);
            var after = NetworkCheck.DefaultRouteProfile();
            _o.WriteLine($"VLCREMOTE MarkPrivate returned {ok}; category now {after?.Category}");
            Assert.True(ok);
            Assert.Equal("Private", after!.Category, ignoreCase: true);
        }
        finally { Ps("Set-NetConnectionProfile -InterfaceIndex " + start.InterfaceIndex + " -NetworkCategory " + original); }
    }
}
