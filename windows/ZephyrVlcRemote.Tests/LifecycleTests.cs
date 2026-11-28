using System.Runtime.InteropServices;
using System.Text.Json;
using ZephyrLifecycle;
using Xunit;

namespace ZephyrVlcRemote.Tests;

// Pure logic only. Firewall, COM, DACL and journal-directory behavior are NOT covered here and stay unproven until a Windows run.
public class LifecycleTests
{
    private static readonly RuleSpec Tcp = new("T (Private)", "Private", "TCP", 8492, "LocalSubnet", "System");
    private static string[] Good(RuleSpec s) => new[] { "True", "Inbound", "Allow", s.Profile, s.Protocol, s.Port.ToString(), "Any", s.Remote, "Any", s.Program, "Any", "Block" };
    private const string K1 = "Zephyr-0123456789abcdef0123456789abcdef";

    [Fact] public void ExactShapeIsExact() => Assert.Equal(RuleState.Exact, ZephyrRules.ClassifyProps(Good(Tcp), Tcp));

    [Theory]
    [InlineData(3, "Public")]
    [InlineData(5, "Any")]
    [InlineData(7, "Any")]
    [InlineData(7, "LocalSubnet4")]
    [InlineData(9, "C:\\other.exe")]
    [InlineData(0, "False")]
    [InlineData(2, "Block")]
    [InlineData(8, "10.0.0.1")]
    [InlineData(10, "Dnscache")]
    [InlineData(11, "Allow")]
    public void AnyDeviationIsMismatch(int idx, string val)
    {
        var f = Good(Tcp); f[idx] = val;
        Assert.Equal(RuleState.Mismatch, ZephyrRules.ClassifyProps(f, Tcp));
    }

    [Fact] public void WrongFieldCountIsMismatch() => Assert.Equal(RuleState.Mismatch, ZephyrRules.ClassifyProps(new[] { "True" }, Tcp));

    [Fact]
    public void ForPortGivesPrivateLocalSubnetAndPublicLocalSubnet4Only()
    {
        var r = ZephyrRules.ForPort("X", "UDP", 8490, "C:\\a.exe");
        Assert.Equal(2, r.Length);
        Assert.Contains(r, x => x.Profile == "Private" && x.Remote == "LocalSubnet" && x.Program == "C:\\a.exe");
        Assert.Contains(r, x => x.Profile == "Public" && x.Remote == "LocalSubnet4");
        Assert.DoesNotContain(r, x => x.Profile.Contains("Domain") || x.Remote == "Any");
    }

    [Theory]
    [InlineData(K1, true)]
    [InlineData("Zephyr-XYZ", false)]
    [InlineData("zephyr-0123456789abcdef0123456789abcdef", false)]
    [InlineData("Zephyr-0123456789ABCDEF0123456789abcdef", false)]
    [InlineData(K1 + "x", false)]
    [InlineData("Zephyr VLC Remote", false)]
    public void KeyPattern(string k, bool ok) => Assert.Equal(ok, System.Text.RegularExpressions.Regex.IsMatch(k, LifecycleJournalStore.KeyPattern));

    [Fact]
    public void JournalValidation()
    {
        var good = new LifecycleJournal(1, new() { new RuleEntry(K1, "d", "p") }, null, new());
        Assert.True(LifecycleJournalStore.IsValid(good));
        Assert.False(LifecycleJournalStore.IsValid(good with { Version = 2 }));
        Assert.False(LifecycleJournalStore.IsValid(good with { Rules = new() { new RuleEntry("by display name", "d", "p") } }));
        Assert.False(LifecycleJournalStore.IsValid(good with { Rules = new() { new RuleEntry(K1, "", "p") } }));
        var wd = new WatchdogEntry(WatchdogIds.TaskName, "S-1-5-21-1-2-3-1001", "D:P", "C:\\a.exe", true);
        Assert.True(LifecycleJournalStore.IsValid(good with { Watchdog = wd }));
        Assert.False(LifecycleJournalStore.IsValid(good with { Watchdog = wd with { Task = "Zephyr VLC Remote Watchdog" } }));   // the legacy name can never be journaled authority
        Assert.False(LifecycleJournalStore.IsValid(good with { Watchdog = wd with { Sid = "S-1-5-18" } }));
    }

    [Fact]
    public void JournalRoundTripsAndRejectsGarbage()
    {
        var j = new LifecycleJournal(1, new() { new RuleEntry(K1, "d", "p") }, null, new() { new UncertainEntry(K1.Replace('0', '1'), "d", "o") });
        var path = Path.Combine(Path.GetTempPath(), "zl-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(j));
            var (st, got) = LifecycleJournalStore.Read(path);
            Assert.Equal(JournalState.Present, st); Assert.Equal(K1, got!.Rules[0].Key);
            File.WriteAllText(path, "{not json");
            Assert.Equal(JournalState.Corrupt, LifecycleJournalStore.Read(path).state);
            File.Delete(path);
            Assert.Equal(JournalState.Absent, LifecycleJournalStore.Read(path).state);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ConflictsAreReportedNotOwned()
    {
        var owned = new HashSet<string> { K1 };
        var ports = new HashSet<int> { 8492 }; var legacy = new HashSet<string> { "Zephyr VLC Remote" };
        string[] Row(string key, string disp, string en, string port, string remote) => new[] { key, disp, en, "Inbound", "Allow", "Public", "TCP", port, remote, "Any" };
        Assert.False(ZephyrRules.IsConflict(Row(K1, "ours", "True", "8492", "LocalSubnet4"), owned, ports, legacy));
        Assert.True(ZephyrRules.IsConflict(Row("x", "someone", "True", "8492", "Any"), owned, ports, legacy));
        Assert.True(ZephyrRules.IsConflict(Row("x", "Zephyr VLC Remote", "True", "9999", "Any"), owned, ports, legacy));
        Assert.False(ZephyrRules.IsConflict(Row("x", "Zephyr VLC Remote", "False", "8492", "Any"), owned, ports, legacy));
        Assert.False(ZephyrRules.IsConflict(Row("x", "other", "True", "1234", "Any"), owned, ports, legacy));
    }

    [Fact]
    public void SddlEquivalence()
    {
        var sid = "S-1-5-21-1-2-3-1001";
        var a = WatchdogSetup.BuildSddl(sid);
        Assert.True(WatchdogSetup.SddlEquivalent(a, "D:P(A;;FR;;;" + sid + ")(A;;FA;;;BA)(A;;FA;;;SY)"));
        Assert.False(WatchdogSetup.SddlEquivalent(a, "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;" + sid + ")"));
        Assert.False(WatchdogSetup.SddlEquivalent(a, "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;S-1-5-21-1-2-3-1002)"));
        Assert.False(WatchdogSetup.SddlEquivalent(a, "garbage"));
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-12-1-1-2-3-4", true)]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-1-0", false)]
    [InlineData("S-1-5-21-1-2-3-1001)(A;;FA;;;WD", false)]
    [InlineData(null, false)]
    public void TargetSidAcceptable(string? sid, bool ok) => Assert.Equal(ok, WatchdogSetup.SidAcceptable(sid));

    [Fact]
    public void TaskXmlRunsAsTargetUserLimitedWithOneAction()
    {
        var x = WatchdogSetup.BuildXml("S-1-5-21-1-2-3-1001", "C:\\a & b\\z.exe");
        Assert.Contains("<UserId>S-1-5-21-1-2-3-1001</UserId>", x);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", x);
        Assert.Contains("C:\\a &amp; b\\z.exe", x);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(x, "<Exec>").Count);
        Assert.Contains("<Arguments>--ensure-running</Arguments>", x);
    }

    [Fact]
    public void TaskExceptionClassifier()
    {
        Assert.Equal(WatchdogSetup.TaskRead.Absent, WatchdogSetup.ClassifyTaskException(new COMException("nf", unchecked((int)0x80070002))));
        Assert.Equal(WatchdogSetup.TaskRead.Absent, WatchdogSetup.ClassifyTaskException(new COMException("nf", unchecked((int)0x80070003))));
        Assert.Equal(WatchdogSetup.TaskRead.Unreadable, WatchdogSetup.ClassifyTaskException(new COMException("denied", unchecked((int)0x80070005))));
        Assert.Equal(WatchdogSetup.TaskRead.Unreadable, WatchdogSetup.ClassifyTaskException(new InvalidOperationException("x")));
        Assert.Equal(WatchdogSetup.TaskRead.Absent, WatchdogSetup.ClassifyTaskException(new InvalidOperationException("w", new COMException("nf", unchecked((int)0x80070002)))));
    }

    [Fact]
    public void ExpectedSpecsHaveNoDomainNoAnyRemote()
    {
        var specs = FirewallSetup.ExpectedSpecs("C:\\a.exe", "C:\\vlc.exe");
        Assert.Equal(5, specs.Length);
        Assert.All(specs, s => { Assert.NotEqual("Any", s.Remote); Assert.DoesNotContain("Domain", s.Profile); });
        Assert.Single(specs, s => s.Program == "C:\\vlc.exe" && s.Profile == "Private");
        Assert.Equal(specs.Length, specs.Select(s => s.Display).Distinct().Count());
    }
}

public class UrlAclWitnessTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void JournaledGrantEquivalentOnlyToTheExactPerUserGrant()
    {
        var grant = SddlUtil.PerUserListenGrant(Sid);
        Assert.True(SddlUtil.Equivalent(grant, "D:(A;;GX;;;" + Sid + ")"));
        Assert.False(SddlUtil.Equivalent(grant, "D:(A;;GX;;;WD)"));                                  // the Everyone grant is not ours
        Assert.False(SddlUtil.Equivalent(grant, "D:(A;;GX;;;" + Sid + ")(A;;GX;;;WD)"));             // a widened descriptor is not unchanged
        Assert.False(SddlUtil.Equivalent(grant, "D:(A;;GA;;;" + Sid + ")"));
        Assert.False(SddlUtil.Equivalent(grant, ""));
    }

    [Fact]
    public void JournalAcceptsOnlyAnHttpsPlusUrl()
    {
        var k = "Zephyr-0123456789abcdef0123456789abcdef";
        var j = new LifecycleJournal(1, new(), null, new(), new UrlAclEntry("https://+:8492/", "D:(A;;GX;;;" + Sid + ")"));
        Assert.True(LifecycleJournalStore.IsValid(j));
        Assert.False(LifecycleJournalStore.IsValid(j with { UrlAcl = new UrlAclEntry("http://+:8492/", "x") }));
        Assert.False(LifecycleJournalStore.IsValid(j with { UrlAcl = new UrlAclEntry("https://+:8492/ ; rm", "x") }));
        Assert.False(LifecycleJournalStore.IsValid(j with { UrlAcl = new UrlAclEntry("https://+:8492/", "") }));
        Assert.NotNull(k);
    }

    [Fact]
    public void OldJournalWithoutUrlAclStillReads()
    {
        var path = Path.Combine(Path.GetTempPath(), "zl-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Version\":1,\"Rules\":[],\"Watchdog\":null,\"Uncertain\":[]}");
            var (st, j) = LifecycleJournalStore.Read(path);
            Assert.Equal(JournalState.Present, st); Assert.Null(j!.UrlAcl);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
