using Xunit;
namespace ZephyrVlcRemote.Tests;

/// <summary>DPAPI at-rest wrapping (round-5): tokens and the pending VLC password are
/// protected per Windows user; migration keeps legacy plain values readable and never
/// destroys an unreadable one.</summary>
public class DpapiTests
{
    [Fact]
    public void WrapThenUnwrapRoundTrips()
    {
        var wrapped = ZephyrVlcRemote.Dpapi.Wrap("token-abc-123");
        Assert.StartsWith("dpapi:", wrapped);
        Assert.Equal("token-abc-123", ZephyrVlcRemote.Dpapi.Unwrap(wrapped));
    }

    [Fact]
    public void WrappedValueNeverContainsThePlainText()
    {
        var wrapped = ZephyrVlcRemote.Dpapi.Wrap("s3cr3t-vlc-password");
        Assert.DoesNotContain("s3cr3t", wrapped);
    }

    [Fact]
    public void LegacyPlainValuesStillRead() =>
        Assert.Equal("plain-old-token", ZephyrVlcRemote.Dpapi.Unwrap("plain-old-token"));

    [Fact]
    public void ACorruptWrappedValueReadsAsNothing_NotAnException() =>
        Assert.Null(ZephyrVlcRemote.Dpapi.Unwrap("dpapi:not-valid-base64!!"));
}
