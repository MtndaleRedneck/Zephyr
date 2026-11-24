using System.Text.Json;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>A refused save must leave memory, disk and the reply in agreement: config POST,
/// phone removal and the Wi-Fi lists roll back, and a good save persists what the phone set.
/// The failure is injected by putting a directory where the config file belongs.</summary>
[Collection("ProgramState")]
public class SaveRollbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-rb-" + Guid.NewGuid().ToString("N"));

    public SaveRollbackTests()
    {
        Directory.CreateDirectory(_dir);
        Program.RedirectDataDirForTests(_dir);
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.DeclinedNetworks.Clear(); Program.HomeNetworks.Clear();
        Program.MoviesFolder = @"C:\Movies"; Program.KeepAwakeEnabled = true; Program.VolumeOsdEnabled = true;
    }

    public void Dispose()
    {
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.DeclinedNetworks.Clear(); Program.HomeNetworks.Clear();
        Program.MoviesFolder = null; Program.KeepAwakeEnabled = true; Program.VolumeOsdEnabled = true;
        Program.ResetDataDirForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void BreakSaves() { if (File.Exists(Program.ConfigPath)) File.Delete(Program.ConfigPath); Directory.CreateDirectory(Program.ConfigPath); }
    private void FixSaves() { if (Directory.Exists(Program.ConfigPath)) Directory.Delete(Program.ConfigPath, true); }
    private static JsonElement Disk() => JsonDocument.Parse(File.ReadAllText(Program.ConfigPath)).RootElement.Clone();

    [Fact]
    public void ConfigChangePersistsKeepAwakeAndVolumeOsdAcrossRestart()
    {
        Assert.True(HttpServer.ApplyConfigChange(@"D:\Films", false, false));
        var d = Disk();
        Assert.Equal(@"D:\Films", d.GetProperty("moviesFolder").GetString());
        Assert.False(d.GetProperty("keepAwake").GetBoolean());
        Assert.False(d.GetProperty("volumeOsd").GetBoolean());
    }

    [Fact]
    public void FailedConfigSaveRestoresAllThreeSettingsAndReportsFailure()
    {
        BreakSaves();
        Assert.False(HttpServer.ApplyConfigChange(@"D:\Films", false, false));
        Assert.Equal(@"C:\Movies", Program.MoviesFolder);
        Assert.True(Program.KeepAwakeEnabled);
        Assert.True(Program.VolumeOsdEnabled);
        FixSaves();
        Assert.True(HttpServer.ApplyConfigChange(@"E:\Later", true, true)); // retry after the cause is fixed persists cleanly
        Assert.Equal(@"E:\Later", Disk().GetProperty("moviesFolder").GetString());
    }

    [Fact]
    public void FailedSingleRemoveKeepsThePhoneAndSuccessRemovesIt()
    {
        var a = new Program.PairedPhone { Token = "ta", Name = "A" };
        var b = new Program.PairedPhone { Token = "tb", Name = "B" };
        lock (Program.PhonesLock) { Program.Phones.Add(a); Program.Phones.Add(b); }
        BreakSaves();
        Assert.False(Program.RemovePhonesAndPersist(all => all.Where(x => ReferenceEquals(x, a)).ToArray()));
        Assert.Equal(new[] { a, b }, Program.PhonesSnapshot());
        FixSaves();
        Assert.True(Program.RemovePhonesAndPersist(all => all.Where(x => ReferenceEquals(x, a)).ToArray()));
        Assert.Equal(new[] { b }, Program.PhonesSnapshot());
        Assert.Single(Disk().GetProperty("phones").EnumerateArray());
    }

    [Fact]
    public void FailedRemoveAllRestoresEveryPhoneInOrder()
    {
        var ps = Enumerable.Range(0, 4).Select(i => new Program.PairedPhone { Token = "t" + i, Name = "P" + i }).ToArray();
        lock (Program.PhonesLock) { Program.Phones.AddRange(ps); }
        BreakSaves();
        Assert.False(Program.RemovePhonesAndPersist(all => all));
        Assert.Equal(ps, Program.PhonesSnapshot());
    }

    [Fact]
    public void FailedWifiChoiceRestoresBothLists()
    {
        Program.HomeNetworks.Add("Home"); Program.DeclinedNetworks.Add("Cafe");
        BreakSaves();
        Assert.False(Program.SetNetworkChoice("Cafe", home: true));
        Assert.Equal(new[] { "Home" }, Program.HomeNetworks);
        Assert.Equal(new[] { "Cafe" }, Program.DeclinedNetworks);
        Assert.False(Program.SetNetworkChoice("Home", home: false));
        Assert.Equal(new[] { "Home" }, Program.HomeNetworks);
        Assert.Equal(new[] { "Cafe" }, Program.DeclinedNetworks);
        FixSaves();
        Assert.True(Program.SetNetworkChoice("Cafe", home: true));
        Assert.Equal(new[] { "Home", "Cafe" }, Program.HomeNetworks);
        Assert.Empty(Program.DeclinedNetworks);
    }

    [Fact]
    public void AReaderUnderTheSaveLockNeverSeesAChangeThatIsAboutToBeRolledBack()
    {
        // Saves keep failing. Each failed change is applied and undone inside the outer save
        // lock, so a reader that takes the same lock must only ever see the prior folder.
        BreakSaves();
        var stop = false;
        var failer = new Thread(() => { while (!Volatile.Read(ref stop)) HttpServer.ApplyConfigChange(@"X:\Never", null, null); });
        failer.Start();
        var seen = new List<string?>();
        for (var i = 0; i < 2000; i++) lock (Program.ConfigSaveLock) { seen.Add(Program.MoviesFolder); }
        Volatile.Write(ref stop, true); failer.Join();
        Assert.DoesNotContain(@"X:\Never", seen);
        Assert.Equal(@"C:\Movies", Program.MoviesFolder);
    }
}
