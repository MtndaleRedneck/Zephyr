using System.Net;
using System.Security.Cryptography;
using SecureLan;
using Xunit;

public class EncodingAndCheckTests
{
    [Fact]
    public void AnchorAndCodeEncodingVectors()
    {
        var a = new byte[16]; for (int i = 0; i < 16; i++) a[i] = (byte)i;
        var c = new byte[8]; for (int i = 0; i < 8; i++) c[i] = (byte)i;
        Assert.Equal("000G40R40M30E209185GR38E1W", Crockford.Encode(a, 26));
        Assert.Equal("000G40R40M30E", Crockford.Encode(c, 13));
    }

    [Fact]
    public void RoundTripAndCanonicalPadding()
    {
        for (int n = 0; n < 200; n++)
        {
            var a = RandomNumberGenerator.GetBytes(16);
            var s = Crockford.Encode(a, 26);
            Assert.True(Crockford.TryDecode(s, 16, out var back));
            Assert.Equal(a, back);
            var c = RandomNumberGenerator.GetBytes(8);
            Assert.True(Crockford.TryDecode(Crockford.Encode(c, 13), 8, out var cb));
            Assert.Equal(c, cb);
        }
    }

    [Fact]
    public void NonzeroPadBitsRejected()
    {
        // last anchor symbol carries 2 pad bits: setting the low bit of 'W'(28) gives 'X'(29)
        var a = new byte[16]; for (int i = 0; i < 16; i++) a[i] = (byte)i;
        var s = Crockford.Encode(a, 26);
        var bad = s.Substring(0, 25) + Crockford.Alphabet[Crockford.Alphabet.IndexOf(s[25]) | 1];
        Assert.False(Crockford.TryDecode(bad, 16, out _));
        var code = Crockford.Encode(new byte[8], 13);   // all zero; last symbol must have low bit zero
        var badCode = code.Substring(0, 12) + "1";
        Assert.False(Crockford.TryDecode(badCode, 8, out _));
        Assert.False(Crockford.TryDecode("0123", 16, out _));   // wrong length
    }

    [Fact]
    public void NormalizationAndGroups()
    {
        Assert.Equal("0011ABC", Crockford.Normalize("oo-Il aBc"));
    }

    [Fact]
    public void ChecksumVectors()
    {
        Assert.Equal(Crockford.Alphabet[0], LinearCheck.Append(new string('0', 26))[^1]);
        Assert.Equal('1', LinearCheck.Append("0123456789ABCDEFGHJKMNPQRS")[^1]);
        Assert.Equal('Q', LinearCheck.Append(new string('Z', 26))[^1]);
        Assert.Equal('B', LinearCheck.Append("0123456789ABC")[^1]);
        Assert.Equal('0', LinearCheck.Append(new string('0', 13))[^1]);
    }

    [Fact]
    public void AllSingleSubstitutionsAndAdjacentSwapsDetected()
    {
        var rnd = new Random(7);
        for (int n = 0; n < 300; n++)
        {
            var chars = Enumerable.Range(0, 26).Select(_ => Crockford.Alphabet[rnd.Next(32)]).ToArray();
            string s = LinearCheck.Append(new string(chars));
            Assert.True(LinearCheck.Verify(s));
            for (int p = 0; p < s.Length; p++)
                foreach (char ch in Crockford.Alphabet)
                    if (ch != s[p]) Assert.False(LinearCheck.Verify(s.Remove(p, 1).Insert(p, ch.ToString())));
            for (int p = 0; p < s.Length - 1; p++)
                if (s[p] != s[p + 1])
                    Assert.False(LinearCheck.Verify(s.Substring(0, p) + s[p + 1] + s[p] + s.Substring(p + 2)));
        }
    }
}

public class PairingWindowTests
{
    sealed class Clock { public DateTimeOffset Now = new(2026, 11, 26, 20, 0, 0, TimeSpan.Zero); public DateTimeOffset Get() => Now; }

    [Fact]
    public void CodeRejectedBeforeOptIn_AndNoCodeInQrOnlyWindow()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        w.Open();
        Assert.False(w.CodeEnabled);
        Assert.Equal(ConsumeResult.Rejected, w.TryConsume(SecretKind.TypedCode, new byte[8], () => true));
        Assert.Equal(WindowState.Open, w.State);   // wrong guess burns nothing
    }

    [Fact]
    public void QrSuccessConsumesBoth_AfterOptIn()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        var qr = w.Open(); var code = w.EnableTypedCode();
        Assert.NotNull(code);
        Assert.Equal(ConsumeResult.Ok, w.TryConsume(SecretKind.Qr, qr, () => true));
        Assert.Equal(WindowState.Consumed, w.State);
        Assert.Equal(ConsumeResult.Rejected, w.TryConsume(SecretKind.TypedCode, code!, () => true));
    }

    [Fact]
    public void CodeSuccessConsumesBoth()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        var qr = w.Open(); var code = w.EnableTypedCode()!;
        Assert.Equal(ConsumeResult.Ok, w.TryConsume(SecretKind.TypedCode, code, () => true));
        Assert.Equal(ConsumeResult.Rejected, w.TryConsume(SecretKind.Qr, qr, () => true));
    }

    [Fact]
    public void WrongGuessDoesNotBurn_ThenRightWorks()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        var qr = w.Open(); var code = w.EnableTypedCode()!;
        Assert.Equal(ConsumeResult.Rejected, w.TryConsume(SecretKind.TypedCode, new byte[8], () => true));
        Assert.Equal(ConsumeResult.Ok, w.TryConsume(SecretKind.TypedCode, code, () => true));
    }

    [Fact]
    public void PersistFailureClosesWindow()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        var qr = w.Open();
        Assert.Equal(ConsumeResult.PersistFailed, w.TryConsume(SecretKind.Qr, qr, () => false));
        Assert.Equal(WindowState.Idle, w.State);
        Assert.Equal(ConsumeResult.Rejected, w.TryConsume(SecretKind.Qr, qr, () => true));
    }

    [Fact]
    public void ExpiryCancelAndNoCodeAfterCancel()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        var qr = w.Open(); w.EnableTypedCode();
        c.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        Assert.Equal(WindowState.Idle, w.State);
        Assert.Equal(ConsumeResult.Rejected, w.TryConsume(SecretKind.Qr, qr, () => true));
        var qr2 = w.Open(); w.EnableTypedCode(); w.Cancel();
        Assert.False(w.CodeEnabled);
        Assert.Null(w.EnableTypedCode());   // not Open
        var qr3 = w.Open();
        Assert.False(w.CodeEnabled);        // reopen starts QR-only again
    }

    [Fact]
    public void EnableTypedCodeOnlyOnce()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        w.Open();
        Assert.NotNull(w.EnableTypedCode());
        Assert.Null(w.EnableTypedCode());
    }

    [Fact]
    public async Task ConcurrentConsumeHasOneWinner()
    {
        var c = new Clock(); var w = new PairingWindow(c.Get);
        var qr = w.Open();
        int ok = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (w.TryConsume(SecretKind.Qr, qr, () => true) == ConsumeResult.Ok) Interlocked.Increment(ref ok);
        })));
        Assert.Equal(1, ok);
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
        l.RecordFailure("a");                       // 5th
        Assert.False(l.IsAllowed("a"));
        c.Now += TimeSpan.FromSeconds(1.1);
        Assert.True(l.IsAllowed("a"));
        l.RecordFailure("a");                       // 6th: 2 s
        Assert.False(l.IsAllowed("a"));
        c.Now += TimeSpan.FromSeconds(1.5);
        Assert.False(l.IsAllowed("a"));
        c.Now += TimeSpan.FromSeconds(1);
        Assert.True(l.IsAllowed("a"));
        c.Now += TimeSpan.FromMinutes(6);           // quiet: reset
        l.RecordFailure("a");
        Assert.True(l.IsAllowed("a"));
    }

    [Fact]
    public void OtherSourcesAreNotBlocked_AndLruIsBounded()
    {
        var c = new Clock(); var l = new SourceLimiter(c.Get, 1024);
        for (int i = 0; i < 5; i++) l.RecordFailure("a");
        Assert.False(l.IsAllowed("a"));
        Assert.True(l.IsAllowed("b"));
        for (int i = 0; i < 3000; i++) l.RecordFailure("s" + i);
        Assert.True(l.Tracked <= 1024);
    }

    [Fact]
    public void KeyNormalizesMappedAddress()
    {
        Assert.Equal("10.0.0.5", SourceLimiter.Key(IPAddress.Parse("::ffff:10.0.0.5")));
    }
}

public class TokenStoreTests
{
    [Fact]
    public void TlsStoreNeverAcceptsLegacyToken_AndLegacyNeverAcceptsTlsToken()
    {
        var tls = TokenStore.CreateSecureTls();
        var legacy = TokenStore.CreateLegacyPlaintext();
        legacy.AddExisting("old-captured-plaintext-token", "old phone");
        Assert.False(tls.Verify("old-captured-plaintext-token", out _));
        var (id, tok) = tls.Issue("new phone");
        Assert.True(tls.Verify(tok, out var got)); Assert.Equal(id, got);
        Assert.False(legacy.Verify(tok, out _));        // reverse test
        Assert.True(legacy.Verify("old-captured-plaintext-token", out _));
    }

    [Fact]
    public void SameStringInBothStoresStaysSeparate()
    {
        var tls = TokenStore.CreateSecureTls();
        var legacy = TokenStore.CreateLegacyPlaintext();
        var (_, tok) = tls.Issue("p");
        legacy.AddExisting(tok, "same string");          // even a identical string must not cross over
        legacy.Revoke(legacy.List()[0].id);
        Assert.True(tls.Verify(tok, out _));
    }

    [Fact]
    public void RevokeIsByIdNotLabel_AndOversizeTokenRejected()
    {
        var tls = TokenStore.CreateSecureTls();
        var (id1, t1) = tls.Issue("Old phone");
        var (id2, t2) = tls.Issue("Old phone");
        Assert.True(tls.Revoke(id1));
        Assert.False(tls.Verify(t1, out _));
        Assert.True(tls.Verify(t2, out _));
        Assert.False(tls.Verify(new string('a', 513), out _));
        Assert.Throws<InvalidOperationException>(() => TokenStore.CreateLegacyPlaintext().Issue("x"));
    }
}

public class DiscoveryFrameTests
{
    static readonly byte[] Magic = { (byte)'Z', (byte)'P', (byte)'L', (byte)'Q' };
    static readonly byte[] RMagic = { (byte)'Z', (byte)'P', (byte)'L', (byte)'R' };

    [Fact]
    public void ProbeReplyRoundTripAndStrictness()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var probe = DiscoveryFrame.BuildProbe(Magic, nonce);
        Assert.True(probe.Length <= 64);
        Assert.True(DiscoveryFrame.TryParseProbe(probe, Magic, out var n)); Assert.Equal(nonce, n);
        Assert.False(DiscoveryFrame.TryParseProbe(probe.Append((byte)0).ToArray(), Magic, out _));
        Assert.False(DiscoveryFrame.TryParseProbe(probe.AsSpan(0, 20), Magic, out _));
        var bad = (byte[])probe.Clone(); bad[4] = 2;
        Assert.False(DiscoveryFrame.TryParseProbe(bad, Magic, out _));
        var reply = DiscoveryFrame.BuildReply(RMagic, nonce, 48443);
        Assert.True(DiscoveryFrame.TryParseReply(reply, RMagic, nonce, out var port)); Assert.Equal(48443, port);
        Assert.False(DiscoveryFrame.TryParseReply(reply, RMagic, RandomNumberGenerator.GetBytes(16), out _));
        var zero = DiscoveryFrame.BuildReply(RMagic, nonce, 0);
        Assert.False(DiscoveryFrame.TryParseReply(zero, RMagic, nonce, out _));
    }
}
