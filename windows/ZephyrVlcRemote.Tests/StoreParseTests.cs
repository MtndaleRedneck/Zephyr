using System.Text.Json;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>Round-5 (Main's catch): one undecryptable record must fail the WHOLE store
/// closed. Skipping it in memory would silently drop the phone's stored token on the
/// next save - the exact data loss the atomic store exists to prevent.</summary>
public class StoreParseTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AllValidRecordsParse()
    {
        var wrapped = Dpapi.Wrap("tok-valid-1");
        var ok = Program.TryParsePhones(Root("{\"phones\":[{\"token\":\"" + wrapped + "\",\"name\":\"Sofa phone\"}]}"),
            out var phones, out var unreadable);
        Assert.True(ok);
        Assert.Null(unreadable);
        Assert.Single(phones);
        Assert.Equal("tok-valid-1", phones[0].Token);
        Assert.Equal("Sofa phone", phones[0].Name);
    }

    [Fact]
    public void MixedValidPlusBadRecordFailsTheWholeStore()
    {
        var wrapped = Dpapi.Wrap("tok-valid-2");
        var json = "{\"phones\":["
            + "{\"token\":\"" + wrapped + "\",\"name\":\"Good phone\"},"
            + "{\"token\":\"dpapi:not-valid-base64!!\",\"name\":\"Torn phone\"}]}";
        var ok = Program.TryParsePhones(Root(json), out var phones, out var unreadable);
        Assert.False(ok);                  // fail closed: the store is broken, not partially loaded
        Assert.Equal("Torn phone", unreadable);
        Assert.Single(phones);             // the good record was read - but the caller refuses to use it
        Assert.Equal("Good phone", phones[0].Name);
    }

    [Fact]
    public void LegacyPlainTokenStillParses()
    {
        var ok = Program.TryParsePhones(Root("{\"token\":\"plain-legacy-token\"}"), out var phones, out _);
        Assert.True(ok);
        Assert.Single(phones);
        Assert.Equal("plain-legacy-token", phones[0].Token);
    }
}
