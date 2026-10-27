using System.Diagnostics;
using System.Security.Principal;
using Xunit;

namespace ZephyrVlcRemote.Tests;

/// <summary>Round-10 B10-1: drives the REAL HttpQueryServiceConfiguration against
/// planted reservations - the native read path the pure classifier tests cannot
/// reach. Requires elevation (the CI windows runner is elevated); asserts rather than
/// skips so a silent non-run can never pass.</summary>
public class LiveUrlAclTests
{
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private const string Url = "http://+:8492/"; // Program.HttpPort

    private static void Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit(15000);
        Assert.True(p.ExitCode == 0, $"netsh {args} exited {p.ExitCode}: {p.StandardOutput.ReadToEnd()}{p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void RealQuery_Absent_Ours_Legacy_And_Foreign()
    {
        Assert.True(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            "the live URL-ACL test needs elevation to plant reservations");
        // Never delete someone else's reservation to make room: on a CI runner the
        // port is free, but on a developer PC a real reservation could exist. REFUSE
        // instead of cleaning foreign state (round-11).
        var existing = Process.Start(new ProcessStartInfo("netsh", "http show urlacl")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        var shown = existing.StandardOutput.ReadToEnd();
        existing.WaitForExit(15000);
        Assert.False(shown.Contains(Url), $"a pre-existing reservation for {Url} was found - refusing to delete foreign state; free it by hand to run this test");
        FirewallSetup.LastReadFailed = false; // isolate from any earlier test's read failure
        try
        {
            Assert.Equal(FirewallSetup.AclOwnership.None, FirewallSetup.ClassifyUrlAcl(Sid));
            Netsh("http add urlacl url=" + Url + " sddl=D:(A;;GX;;;SY)");
            Assert.Equal(FirewallSetup.AclOwnership.Foreign, FirewallSetup.ClassifyUrlAcl(Sid));
            Netsh("http delete urlacl url=" + Url);
            Netsh("http add urlacl url=" + Url + " sddl=D:(A;;GX;;;" + Sid + ")");
            Assert.Equal(FirewallSetup.AclOwnership.Ours, FirewallSetup.ClassifyUrlAcl(Sid));
            Netsh("http delete urlacl url=" + Url);
            Netsh("http add urlacl url=" + Url + " sddl=D:(A;;GX;;;WD)");
            Assert.Equal(FirewallSetup.AclOwnership.Legacy, FirewallSetup.ClassifyUrlAcl(Sid));
            Assert.False(FirewallSetup.LastReadFailed);
        }
        finally { Netsh("http delete urlacl url=" + Url); }
    }
}
