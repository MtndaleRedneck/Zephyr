using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>The pair endpoint when the vlcrc step fails AFTER the phone is saved and the code spent: the phone
/// must still get its token (no orphan), and an existing VLC password must never be replaced.
/// ProgramState runs alone, so port 8492 is not shared with the HTTP reservation tests.</summary>
[Collection("ProgramState")]
public class PairVlcrcFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-pairfail-" + Guid.NewGuid().ToString("N"));
    private string VlcDirPath => Path.Combine(_dir, "vlc");

    public PairVlcrcFailureTests()
    {
        Directory.CreateDirectory(_dir);
        Program.RedirectDataDirForTests(_dir);
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.PairCode = "1234"; Program.PairCodeIssuedUtc = DateTime.UtcNow; Program.PairFailures = 0; Program.PendingPhoneName = null;
        VlcSetup.VlcDirOverride = VlcDirPath;
        VlcSetup.VlcRunningOverride = () => false;
        VlcSetup.ForgetCandidateForTests();
        VlcRotation.ResetForTests();
        VlcRotation.BackgroundDisabledForTests = true;  // no pool pass may leak into the next test
    }

    public void Dispose()
    {
        VlcRotation.BackgroundDisabledForTests = false;
        VlcRotation.AwaitQuiescenceForTests(TimeSpan.FromSeconds(5));
        VlcSetup.VlcDirOverride = null; VlcSetup.VlcRunningOverride = null; VlcSetup.ForgetCandidateForTests(); VlcRotation.ResetForTests();
        Program.PairCode = null; Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.ResetDataDirForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Task<(int Status, JsonElement Body)> PairAsync() => PairAsyncWithCode("1234");

    private static async Task<(int Status, JsonElement Body)> PairAsyncWithCode(string code)
    {
        HelperTestTls.EnsureReady();
        using var server = new HttpServer(); server.Start();
        using var client = HelperTestTls.Client();
        using var res = await client.PostAsync($"https://127.0.0.1:{Program.HttpPort}/pair", new StringContent("{\"code\":\"" + code + "\"}", Encoding.UTF8, "application/json"));
        var text = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task PairStillReturnsTheToken_WhenVlcrcCannotBeWritten()
    {
        File.WriteAllText(VlcDirPath, "x");                       // a file where the vlc directory must go
        var (status, body) = await PairAsync();
        Assert.Equal(200, status);
        var token = body.GetProperty("token").GetString()!;
        Assert.NotEmpty(token);
        Assert.Equal(24, body.GetProperty("vlcPassword").GetString()!.Length);
        Assert.True(body.GetProperty("vlcPending").GetBoolean());  // the existing "not on disk yet" meaning
        Assert.Equal(1, Program.PhonesCount);                      // the saved phone has its token
        Assert.Null(Program.PairCode);                             // the code is spent exactly once
        Assert.Contains(token, JsonDocument.Parse(File.ReadAllText(Program.ConfigPath)).RootElement.GetProperty("phones")
            .EnumerateArray().Select(p => Dpapi.Unwrap(p.GetProperty("token").GetString()!)));
    }

    [Fact]
    public async Task PairIsRefusedRetryably_WhenAnExistingVlcrcCannotBeRead_ThenSucceedsWithTheSamePassword()
    {
        Directory.CreateDirectory(VlcDirPath);
        var vlcrc = Path.Combine(VlcDirPath, "vlcrc");
        File.WriteAllLines(vlcrc, new[] { "extraintf=http", "http-host=0.0.0.0", "http-password=existing-password-1" });
        var before = File.ReadAllBytes(vlcrc);
        int status; JsonElement body;
        using (new FileStream(vlcrc, FileMode.Open, FileAccess.Read, FileShare.None))   // a transient lock
        {
            (status, body) = await PairAsync();
            Assert.Equal(503, status);                                                  // retryable, no token
            Assert.False(body.TryGetProperty("token", out _));
            Assert.Equal(0, Program.PhonesCount);                                       // no phone saved
            Assert.Equal("1234", Program.PairCode);                                     // the code is not spent
            Assert.Equal(0, Program.PairFailures);
            Assert.False(VlcSetup.Pending);                                             // nothing generated or queued
        }
        Assert.Equal(before, File.ReadAllBytes(vlcrc));                                 // vlcrc bytes unchanged
        (status, body) = await PairAsync();                                             // the same code, lock gone
        Assert.Equal(200, status);
        Assert.NotEmpty(body.GetProperty("token").GetString()!);
        Assert.Equal("existing-password-1", body.GetProperty("vlcPassword").GetString());
        Assert.Equal(1, Program.PhonesCount);
        Assert.Null(Program.PairCode);
    }

    [Fact]
    public async Task AWrongCode_ChangesNothingInVlcSetup()
    {
        var (status, _) = await PairAsyncWithCode("9999");
        Assert.Equal(403, status);
        Assert.False(VlcSetup.Pending);
        Assert.False(Directory.Exists(VlcDirPath));                                     // nothing generated or written
        Assert.Equal(0, Program.PhonesCount);
    }
}
