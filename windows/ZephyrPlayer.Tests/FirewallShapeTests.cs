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
        bool enabled, int profiles, string remote, string program) =>
        new(name, protocol, ports, direction, enabled, profiles, remote, program);

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
}
