using System.Collections.Generic;
using Xunit;

namespace ZephyrPlayer.Tests;

/// <summary>Round-8 N-3/N-4: the shape predicate over planted rule sets - the same
/// decision the unelevated COM read feeds, driven headless. A stale broader rule beside
/// a correct one or any non-Private bit must fail; the 1.1.0 shape must fail.</summary>
public class FirewallShapeTests
{
    private const string Exe = @"C:\Users\test\AppData\Local\Programs\Zephyr Player\ZephyrPlayer.exe";

    private static FirewallSetup.RuleShape Rule(string name, int protocol, string ports, int direction,
        bool enabled, int profiles, string remote, string program, int action = 1) =>
        new(name, protocol, ports, direction, enabled, profiles, remote, program, action);

    private static FirewallSetup.RuleShape GoodTcp() =>
        Rule(FirewallSetup.TcpRuleName, 6, PlaybackServer.DefaultPort.ToString(), 1, true, 2, "LocalSubnet", "System");
    private static FirewallSetup.RuleShape GoodUdp() =>
        Rule(FirewallSetup.UdpRuleName, 17, DiscoveryResponder.OwnPort.ToString(), 1, true, 2, "LocalSubnet", Exe);

    [Fact]
    public void CurrentShapePasses()
    {
        Assert.True(FirewallSetup.RulesHaveCurrentShape(new List<FirewallSetup.RuleShape> { GoodTcp(), GoodUdp() }, Exe));
    }

    [Fact]
    public void Shape110Fails()
    {
        // The published 1.1.0: TCP any-program any-address, UDP on BingeMote's 8489.
        var tcp110 = Rule(FirewallSetup.TcpRuleName, 6, PlaybackServer.DefaultPort.ToString(), 1, true, 2, "*", "Any");
        var udp110 = Rule(FirewallSetup.UdpRuleName, 17, "8489", 1, true, 2, "*", "Any");
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new List<FirewallSetup.RuleShape> { tcp110, udp110 }, Exe));
    }

    [Fact]
    public void StaleBroaderDuplicateFails()
    {
        var broad = Rule(FirewallSetup.TcpRuleName, 6, PlaybackServer.DefaultPort.ToString(), 1, true, 0x7FFFFFFF, "*", "Any");
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new List<FirewallSetup.RuleShape> { GoodTcp(), broad, GoodUdp() }, Exe));
    }

    [Fact]
    public void PublicProfileFails()
    {
        var pub = Rule(FirewallSetup.TcpRuleName, 6, PlaybackServer.DefaultPort.ToString(), 1, true, 2 | 4, "LocalSubnet", "System");
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new List<FirewallSetup.RuleShape> { pub, GoodUdp() }, Exe));
    }

    [Fact]
    public void MissingUdpFails()
    {
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new List<FirewallSetup.RuleShape> { GoodTcp() }, Exe));
    }

    [Fact]
    public void ComFailureFailsClosed()
    {
        Assert.False(FirewallSetup.RulesHaveCurrentShape(null, Exe));
    }

    [Fact]
    public void BlockRuleFails()
    {
        // Round-9 N9-4: a same-named BLOCK rule with otherwise-perfect shape is not ours.
        var block = Rule(FirewallSetup.TcpRuleName, 6, PlaybackServer.DefaultPort.ToString(), 1, true, 2, "LocalSubnet", "System", action: 0);
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new List<FirewallSetup.RuleShape> { block, GoodUdp() }, Exe));
    }
}

/// <summary>Round-9 T4: the URL-ACL ownership matrix through the pure descriptor
/// classifier - the same decision the elevated setup and the uninstall cleanup make.
/// Only an exact per-user grant or the exact 1.1.0 Everyone grant may ever be treated
/// as removable; everything else is foreign and is never touched.</summary>
public class UrlAclOwnershipTests
{
    private const string User = "S-1-5-21-100-200-300-1001";

    [Fact]
    public void ExactUserGrantIsOurs()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Ours,
            FirewallSetup.ClassifySddl("D:(A;;GX;;;" + User + ")", User));
    }

    [Fact]
    public void SidPrefixCollisionIsForeign()
    {
        // ...-1001 must never "own" a reservation that names ...-10012.
        Assert.Equal(FirewallSetup.AclOwnership.Foreign,
            FirewallSetup.ClassifySddl("D:(A;;GX;;;S-1-5-21-100-200-300-10012)", User));
    }

    [Fact]
    public void MultiAceContainingUserIsForeign()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Foreign,
            FirewallSetup.ClassifySddl("D:(A;;GX;;;BA)(A;;GX;;;" + User + ")", User));
    }

    [Fact]
    public void EveryonePlusAnotherAceIsForeign()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Foreign,
            FirewallSetup.ClassifySddl("D:(A;;GX;;;WD)(A;;GX;;;" + User + ")", User));
    }

    [Fact]
    public void DenyEveryoneIsForeign()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Foreign,
            FirewallSetup.ClassifySddl("D:(D;;GX;;;WD)", User));
    }

    [Fact]
    public void LegacyEveryoneGrantIsLegacy()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Legacy,
            FirewallSetup.ClassifySddl("D:(A;;GX;;;WD)", User));
    }

    [Fact]
    public void SystemGrantIsForeign()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Foreign,
            FirewallSetup.ClassifySddl("D:(A;;GX;;;SY)", User));
    }

    [Fact]
    public void UnparsableIsForeign()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Foreign,
            FirewallSetup.ClassifySddl("not-an-sddl", User));
    }

    [Fact]
    public void EmptyIsForeign()
    {
        Assert.Equal(FirewallSetup.AclOwnership.Foreign, FirewallSetup.ClassifySddl(null, User));
        Assert.Equal(FirewallSetup.AclOwnership.Foreign, FirewallSetup.ClassifySddl("", User));
    }

    [Fact]
    public void AliasesNormalizeToSameDescriptor()
    {
        // The classifier compares normalized descriptors, so netsh/http.sys alias forms
        // (Everyone stored as the SID or as WD) classify identically.
        Assert.Equal(
            FirewallSetup.ClassifySddl("D:(A;;GX;;;S-1-1-0)", User),
            FirewallSetup.ClassifySddl("D:(A;;GX;;;WD)", User));
    }
}
