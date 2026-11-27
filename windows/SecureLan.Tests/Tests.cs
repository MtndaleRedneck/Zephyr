using System.Net;
using System.Security.Cryptography;
using SecureLan;
using Xunit;

public class PinStoreTests
{
    [Fact]
    public void FirstUseOnlyDuringExplicitEnrollment_ThenMatchAndHardFailOnChange()
    {
        var s = new PinStore();
        var keyA = RandomNumberGenerator.GetBytes(91); var keyB = RandomNumberGenerator.GetBytes(91);
        Assert.Equal(PinCheck.Mismatch, s.Check("pc1", keyA, enroll: false, out _));          // discovery never enrolls
        Assert.Equal(PinCheck.FirstUseRemembered, s.Check("pc1", keyA, enroll: true, out var h));
        Assert.Equal(PinCheck.Mismatch, s.Check("pc1", keyA, false, out _));                   // nothing stored until Commit
        Assert.True(s.Commit("pc1", h));
        Assert.Equal(PinCheck.Match, s.Check("pc1", keyA, false, out _));
        Assert.Equal(PinCheck.Mismatch, s.Check("pc1", keyB, false, out _));
        Assert.Equal(PinCheck.Mismatch, s.Check("pc1", keyB, true, out var hb));               // enroll flag cannot replace an existing pin
        Assert.False(s.Commit("pc1", hb));
        Assert.Equal(PinCheck.Match, s.Check("pc1", keyA, false, out _));
    }

    [Fact]
    public void ExplicitForgetAllowsRepairAndPinsAreIndependentPerPc()
    {
        var s = new PinStore();
        var a = RandomNumberGenerator.GetBytes(91); var b = RandomNumberGenerator.GetBytes(91);
        s.Check("pc1", a, true, out var ha); s.Commit("pc1", ha);
        s.Check("pc2", b, true, out var hb); s.Commit("pc2", hb);
        Assert.Equal(PinCheck.Mismatch, s.Check("pc1", b, false, out _));
        Assert.True(s.Forget("pc1"));
        Assert.Equal(PinCheck.FirstUseRemembered, s.Check("pc1", b, true, out _));
        Assert.Equal(PinCheck.Match, s.Check("pc2", b, false, out _));
        Assert.False(s.Commit("pcX", new byte[5]));
    }
}

public class LimiterTests
{
    sealed class Clock { public DateTimeOffset Now = new(2026, 11, 26, 20, 0, 0, TimeSpan.Zero); public DateTimeOffset Get() => Now; }

    [Fact]
    public void FiveFailuresBlockThenBackoffGrowsAndResets()
    {
        var c = new Clock(); var l = new SourceLimiter(c.Get);
        for (int i = 0; i < 4; i++) { Assert.True(l.IsAllowed("a")); l.RecordFailure("a"); }
        Assert.True(l.IsAllowed("a"));
        l.RecordFailure("a");
        Assert.False(l.IsAllowed("a"));
        c.Now += TimeSpan.FromSeconds(1.1);
        Assert.True(l.IsAllowed("a"));
        l.RecordFailure("a");
        Assert.False(l.IsAllowed("a"));
        c.Now += TimeSpan.FromSeconds(2.5);
        Assert.True(l.IsAllowed("a"));
        c.Now += TimeSpan.FromMinutes(6);
        l.RecordFailure("a");
        Assert.True(l.IsAllowed("a"));
    }

    [Fact]
    public void WindowRolloverAfterOneMinuteResetsCount()
    {
        var c = new Clock(); var l = new SourceLimiter(c.Get);
        for (int i = 0; i < 4; i++) l.RecordFailure("a");
        c.Now += TimeSpan.FromSeconds(61);
        l.RecordFailure("a");                 // count restarts at 1, not 5
        Assert.True(l.IsAllowed("a"));
    }

    [Fact]
    public void OtherSourcesNotBlocked_LruBounded_MappedAddressNormalized()
    {
        var c = new Clock(); var l = new SourceLimiter(c.Get, 1024);
        for (int i = 0; i < 5; i++) l.RecordFailure("a");
        Assert.False(l.IsAllowed("a")); Assert.True(l.IsAllowed("b"));
        for (int i = 0; i < 3000; i++) l.RecordFailure("s" + i);
        Assert.True(l.Tracked <= 1024);
        Assert.Equal("10.0.0.5", SourceLimiter.Key(IPAddress.Parse("::ffff:10.0.0.5")));
    }
}

public class TokenStoreTests
{
    [Fact]
    public void SecureAndLegacyStoresNeverCrossAccept()
    {
        var tls = TokenStore.CreateSecureTls(); var legacy = TokenStore.CreateLegacyPlaintext();
        legacy.AddExisting("old-captured-plaintext-token", "old phone");
        Assert.False(tls.Verify("old-captured-plaintext-token", out _));
        var (id, tok) = tls.Issue("new phone");
        Assert.True(tls.Verify(tok, out var got)); Assert.Equal(id, got);
        Assert.False(legacy.Verify(tok, out _));
        Assert.True(legacy.Verify("old-captured-plaintext-token", out _));
        legacy.AddExisting(tok, "same string");
        Assert.False(tls.Verify("x" + tok, out _));
    }

    [Fact]
    public void RevokeByIdNotLabel_OversizeRejected_LegacyNeverIssues()
    {
        var tls = TokenStore.CreateSecureTls();
        var (id1, t1) = tls.Issue("Old phone"); var (_, t2) = tls.Issue("Old phone");
        Assert.True(tls.Revoke(id1));
        Assert.False(tls.Verify(t1, out _)); Assert.True(tls.Verify(t2, out _));
        Assert.False(tls.Verify(new string('a', 513), out _));
        Assert.Throws<InvalidOperationException>(() => TokenStore.CreateLegacyPlaintext().Issue("x"));
    }
}

public class DiscoveryFrameTests
{
    static readonly byte[] Magic = { (byte)'Z', (byte)'P', (byte)'L', (byte)'Q' };
    static readonly byte[] RMagic = { (byte)'Z', (byte)'P', (byte)'L', (byte)'R' };

    [Fact]
    public void ProbeReplyRoundTripAndStrictness_AndSmallAmplification()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var probe = DiscoveryFrame.BuildProbe(Magic, nonce);
        var reply = DiscoveryFrame.BuildReply(RMagic, nonce, 48443);
        Assert.True(probe.Length <= 64 && reply.Length <= 64);
        Assert.True(reply.Length < probe.Length * 1.2);          // 23 vs 21 bytes: about 1.1x, no meaningful amplification
        Assert.True(DiscoveryFrame.TryParseProbe(probe, Magic, out var n)); Assert.Equal(nonce, n);
        Assert.False(DiscoveryFrame.TryParseProbe(probe.Append((byte)0).ToArray(), Magic, out _));
        var bad = (byte[])probe.Clone(); bad[4] = 2;
        Assert.False(DiscoveryFrame.TryParseProbe(bad, Magic, out _));
        Assert.True(DiscoveryFrame.TryParseReply(reply, RMagic, nonce, 48443, out var port)); Assert.Equal(48443, port);
        Assert.False(DiscoveryFrame.TryParseReply(reply, RMagic, RandomNumberGenerator.GetBytes(16), 48443, out _));
        Assert.False(DiscoveryFrame.TryParseReply(DiscoveryFrame.BuildReply(RMagic, nonce, 4444), RMagic, nonce, 48443, out _));   // wrong port refused
        Assert.False(DiscoveryFrame.TryParseReply(DiscoveryFrame.BuildReply(RMagic, nonce, 0), RMagic, nonce, 48443, out _));
    }
}

public class LimiterSuccessTests
{
    [Fact]
    public void SuccessClearsSourceFailures()
    {
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var l = new SourceLimiter(() => now);
        for (int i = 0; i < 5; i++) l.RecordFailure("a");
        Assert.False(l.IsAllowed("a"));
        l.RecordSuccess("a");
        Assert.True(l.IsAllowed("a"));
    }
}

public class ReplyBudgetTests
{
    [Fact]
    public void TableCapRefusesNewSourcesWithinTheSecond()
    {
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var b = new ReplyBudget(() => now, perSourceMax: 2, globalMax: 1000, tableMax: 256);
        int granted = 0;
        for (int i = 0; i < 1000; i++) if (b.TryTake("s" + i)) granted++;
        Assert.Equal(256, granted);                 // saturation: bounded memory, extra sources get nothing this second
        Assert.True(b.TryTake("s0"));               // an already-tracked source still has its second reply
    }

{
    [Fact]
    public void PerSourceTwoGlobalTwentyPerSecond_ResetsNextSecond()
    {
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var b = new ReplyBudget(() => now);
        Assert.True(b.TryTake("a")); Assert.True(b.TryTake("a")); Assert.False(b.TryTake("a"));
        int granted = 0;
        for (int i = 0; i < 100; i++) if (b.TryTake("s" + i)) granted++;
        Assert.Equal(18, granted);
        Assert.False(b.TryTake("zzz"));
        now += TimeSpan.FromSeconds(1);
        Assert.True(b.TryTake("a"));
    }
}
