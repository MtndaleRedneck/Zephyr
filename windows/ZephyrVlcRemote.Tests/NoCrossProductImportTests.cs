using System.Text.Json;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>A fresh Zephyr VLC Remote never authorizes phones from another product's config,
/// and an existing Zephyr config keeps its own pairings.</summary>
[Collection("ProgramState")]
public class NoCrossProductImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-nox-" + Guid.NewGuid().ToString("N"));
    private readonly string _legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BingeMote");
    private readonly string _legacyFile;
    private bool _plantedLegacy;

    public NoCrossProductImportTests()
    {
        _legacyFile = Path.Combine(_legacyDir, "config.json");
        Directory.CreateDirectory(_dir);
        Program.RedirectDataDirForTests(_dir);
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
    }

    public void Dispose()
    {
        if (_plantedLegacy) { try { File.Delete(_legacyFile); Directory.Delete(_legacyDir); } catch { } }
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.ResetDataDirForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void PlantBingeConfig()
    {
        if (File.Exists(_legacyFile)) return; // never touch a real install; the assertion below still holds
        Directory.CreateDirectory(_legacyDir);
        File.WriteAllText(_legacyFile, "{\"phones\":[{\"token\":\"binge-token-1\",\"name\":\"Binge Phone\"}],\"moviesFolder\":\"C:\\\\Movies\"}");
        _plantedLegacy = true;
    }

    [Fact]
    public void Fresh_install_with_a_BingeMote_config_starts_unpaired()
    {
        PlantBingeConfig();
        Program.LoadConfig();
        Assert.Equal(0, Program.PhonesCount);
        Assert.False(File.Exists(Program.ConfigPath));
    }

    [Fact]
    public void Existing_Zephyr_config_keeps_its_own_pairings()
    {
        PlantBingeConfig();
        Program.AddPhoneAndPersist("zephyr-token-1", "Zephyr Phone");
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.LoadConfig();
        Assert.Equal(1, Program.PhonesCount);
        lock (Program.PhonesLock) { Assert.Equal("Zephyr Phone", Program.Phones[0].Name); Assert.Equal("zephyr-token-1", Program.Phones[0].Token); }
    }

    [Fact]
    public void The_import_type_is_gone()
    {
        Assert.Null(typeof(Program).Assembly.GetType("ZephyrVlcRemote.BingeMigration"));
    }
}
