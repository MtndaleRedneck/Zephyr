using Xunit;

namespace ZephyrVlcRemote.Tests;

/// <summary>Round-8 N-3/N-4: the shape predicate over planted rule sets - the same
/// decision the unelevated COM read feeds, driven headless. A stale broader rule beside
/// a correct one, a leftover legacy rule, or any non-Private bit must ALL fail.</summary>
[Collection("ProgramState")]
public class FirewallShapeTests
{
    private const string Exe = @"C:\Users\test\AppData\Local\Programs\Zephyr VLC Remote\ZephyrVlcRemote.exe";

    private static FirewallSetup.RuleShape Rule(string name, int protocol, string ports, int direction,
        bool enabled, int profiles, string remote, string program) =>
        new(name, protocol, ports, direction, enabled, profiles, remote, program);

    private static FirewallSetup.RuleShape GoodTcp() =>
        Rule(Program.FirewallRule, 6, Program.HttpPort.ToString(), 1, true, 2, "LocalSubnet", "System");
    private static FirewallSetup.RuleShape GoodUdp() =>
        Rule(Program.FirewallRule, 17, Program.DiscoveryPort.ToString(), 1, true, 2, "LocalSubnet", Exe);

    [Fact]
    public void CurrentShapePasses()
    {
        Assert.True(FirewallSetup.RulesHaveCurrentShape(new() { GoodTcp(), GoodUdp() }, Exe));
    }

    [Fact]
    public void OldExeScopedTcpFails()
    {
        // The 1.x shape: TCP scoped to the exe instead of System.
        var old = Rule(Program.FirewallRule, 6, Program.HttpPort.ToString(), 1, true, 2, "LocalSubnet", Exe);
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new() { old, GoodUdp() }, Exe));
    }

    [Fact]
    public void LegacyRuleMustBeGone()
    {
        var legacy = Rule(Program.LegacyFirewallRule, 6, Program.HttpPort.ToString(), 1, true, 2, "LocalSubnet", Exe);
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new() { GoodTcp(), GoodUdp(), legacy }, Exe));
    }

    [Fact]
    public void StaleBroaderDuplicateFails()
    {
        // A correct rule beside a same-named broad one: ANY non-matching block fails (N-3).
        var broad = Rule(Program.FirewallRule, 6, Program.HttpPort.ToString(), 1, true, 0x7FFFFFFF, "*", "Any");
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new() { GoodTcp(), broad, GoodUdp() }, Exe));
    }

    [Fact]
    public void PublicOrDomainProfileFails()
    {
        var pub = Rule(Program.FirewallRule, 6, Program.HttpPort.ToString(), 1, true, 2 | 4, "LocalSubnet", "System");
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new() { pub, GoodUdp() }, Exe));
    }

    [Fact]
    public void AnyRemoteAddressFails()
    {
        var any = Rule(Program.FirewallRule, 6, Program.HttpPort.ToString(), 1, true, 2, "*", "System");
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new() { any, GoodUdp() }, Exe));
    }

    [Fact]
    public void MissingUdpFails()
    {
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new() { GoodTcp() }, Exe));
    }

    [Fact]
    public void DisabledRuleFails()
    {
        var off = Rule(Program.FirewallRule, 17, Program.DiscoveryPort.ToString(), 1, false, 2, "LocalSubnet", Exe);
        Assert.False(FirewallSetup.RulesHaveCurrentShape(new() { GoodTcp(), off }, Exe));
    }

    [Fact]
    public void ComFailureFailsClosed()
    {
        Assert.False(FirewallSetup.RulesHaveCurrentShape(null, Exe));
    }
}
