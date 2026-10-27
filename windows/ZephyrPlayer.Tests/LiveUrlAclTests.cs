using System.Diagnostics;
using System.Security.Principal;
using Xunit;

namespace ZephyrPlayer.Tests;

/// <summary>Round-10 B10-1: drives the REAL HttpQueryServiceConfiguration against
/// planted reservations. The pure classifier tests cannot reach the native read path -
/// a struct/signature bug there made every read fail closed and only CI exit codes
/// caught it. Requires elevation to plant reservations (the CI windows runner is
/// elevated); it asserts rather than skips so a silent non-run can never pass.</summary>
public class LiveUrlAclTests
{
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private const string Url = "http://+:47823/"; // PlaybackServer.DefaultPort

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
    }

    [Fact]
    public void RealQuery_Absent_Ours_Legacy_And_Foreign()
    {
        Assert.True(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            "the live URL-ACL test needs elevation to plant reservations");
        Netsh("http delete urlacl url=" + Url);
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
