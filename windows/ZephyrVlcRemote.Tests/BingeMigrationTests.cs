using Xunit;
namespace ZephyrVlcRemote.Tests;

/// <summary>The BingeMote-to-Zephyr pairing migration: preserves what the settled scope
/// asks for, never touches the legacy file, never overwrites an existing own config.</summary>
public class BingeMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvr-mig-" + Guid.NewGuid().ToString("N"));
    private string LegacyPath => Path.Combine(_dir, "legacy", "config.json");
    private string OwnPath => Path.Combine(_dir, "own", "config.json");

    public BingeMigrationTests() => Directory.CreateDirectory(Path.Combine(_dir, "legacy"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Phones_And_Choices_Are_Carried_Over()
    {
        const string legacyText = """
            {"phones":[{"token":"tok-a","name":"Living room phone","adoptSpent":true,"adoptRequestId":"req-7","adoptServedAtUtc":"2026-10-20T00:00:00Z"},
                       {"token":"tok-b","name":"Second phone"}],
             "moviesFolder":"D:\\Films","declinedNetworks":["CafeWiFi"],"homeNetworks":["HomeLAN"]}
            """;
        File.WriteAllText(LegacyPath, legacyText);
        Assert.True(BingeMigration.TryImport(LegacyPath, OwnPath));
        var own = File.ReadAllText(OwnPath);
        Assert.Contains("tok-a", own); Assert.Contains("Living room phone", own);
        Assert.Contains("tok-b", own);
        var roundTripped = BingeMigration.Parse(own); // our own loader's view of the import
        Assert.NotNull(roundTripped);
        Assert.Equal("D:\\Films", roundTripped!.MoviesFolder);
        Assert.Contains("CafeWiFi", own); Assert.Contains("HomeLAN", own);
        // The legacy file is BYTE-identical afterwards: strictly read-only.
        Assert.Equal(legacyText, File.ReadAllText(LegacyPath));
    }

    [Fact]
    public void Spent_Grant_Stays_Spent_In_Flight_Grant_Is_Dropped()
    {
        var parsed = BingeMigration.Parse("""
            {"phones":[{"token":"t1","name":"p1","adoptSpent":true,"adoptRequestId":"r1","adoptServedAtUtc":"2026-10-20T00:00:00Z"}]}
            """);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Phones);
        Assert.True(parsed.Phones[0].AdoptSpent); // the phone already has the VLC password; never re-serve blindly
        var json = Path.Combine(_dir, "o.json");
        BingeMigration.WriteAsOwnConfig(parsed, json);
        var written = File.ReadAllText(json);
        Assert.Contains("\"adoptSpent\":true", written.Replace(" ", ""));
        Assert.DoesNotContain("adoptRequestId", written); // no in-flight grant crosses over
    }

    [Fact]
    public void Legacy_Tokens_Shape_Still_Imports()
    {
        var parsed = BingeMigration.Parse("""{"tokens":["a","b"]}""");
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Phones.Count);
        Assert.Equal("Phone 1", parsed.Phones[0].Name);
    }

    [Fact]
    public void Existing_Own_Config_Is_Never_Overwritten_And_Empty_Legacy_Imports_Nothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OwnPath)!);
        File.WriteAllText(OwnPath, """{"phones":[{"token":"mine","name":"My phone"}]}""");
        File.WriteAllText(LegacyPath, """{"phones":[{"token":"theirs","name":"Other"}]}""");
        Assert.False(BingeMigration.TryImport(LegacyPath, OwnPath));
        Assert.Contains("mine", File.ReadAllText(OwnPath));
        Assert.DoesNotContain("theirs", File.ReadAllText(OwnPath));

        Assert.Null(BingeMigration.Parse("{}"));

        // Malformed or unreadable legacy: no success, no own file created, nothing erased.
        File.Delete(OwnPath); // the "nothing created" half starts from no own file
        File.WriteAllText(LegacyPath, "{ this is not json ");
        Assert.False(BingeMigration.TryImport(LegacyPath, OwnPath));
        Assert.False(File.Exists(OwnPath));
        Assert.Equal("{ this is not json ", File.ReadAllText(LegacyPath));
        File.WriteAllText(LegacyPath, "{}");
        Assert.False(BingeMigration.TryImport(LegacyPath, OwnPath));
    }
}
