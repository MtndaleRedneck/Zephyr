using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace ZephyrPlayer.Tests
{
    /// <summary>CI-only (BM_NET_PROOF=1, disposable runner): the production Player probe must read the runner's real category,
    /// before and after the runner's category is changed by this test's own setup. Read-only product code; not Wi-Fi.</summary>
    public class NetProbeProofTests
    {
        private readonly ITestOutputHelper _o;
        public NetProbeProofTests(ITestOutputHelper o) { _o = o; }
        private static string Ps(string s)
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"" + s + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!; p.WaitForExit(30000);
            return (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        }
        [Fact]
        public void PlayerProbeReadsRealCategoryOnDisposableRunner()
        {
            if (Environment.GetEnvironmentVariable("BM_NET_PROOF") != "1") return;
            var start = NetworkCheck.DefaultRouteProfile();
            Assert.NotNull(start);
            _o.WriteLine($"PLAYER START name='{start!.Name}' category={start.Category} ifIndex={start.InterfaceIndex}");
            var original = start.Category;
            try
            {
                foreach (var cat in new[] { "Private", "Public" })
                {
                    Ps("Set-NetConnectionProfile -InterfaceIndex " + start.InterfaceIndex + " -NetworkCategory " + cat);
                    var p = NetworkCheck.DefaultRouteProfile();
                    _o.WriteLine($"PLAYER after setting {cat}: probe category={p?.Category}");
                    Assert.NotNull(p);
                    Assert.Equal(cat, p!.Category, ignoreCase: true);
                }
            }
            finally { Ps("Set-NetConnectionProfile -InterfaceIndex " + start.InterfaceIndex + " -NetworkCategory " + original); }
        }
    }
}
