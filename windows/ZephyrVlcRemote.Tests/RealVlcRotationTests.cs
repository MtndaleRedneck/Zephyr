using System.Diagnostics;
using System.Text.Json;
using ZephyrVlcRemote;
using Xunit;

namespace ZephyrVlcRemote.Tests;

/// <summary>REAL VLC (the helper-realvlc lane installs it via choco and provides a video
/// fixture): the rotation against a live vlc.exe and its live HTTP interface. helper.yml's
/// FullyQualifiedName!~RealVlc filter keeps these out of the source-only lane; they fail hard
/// without the choco VLC or the fixture, exactly like RealVlcAddsSidecar. No VlcRunningOverride
/// here: the pass must detect the REAL process. The tests drive VLC's graceful exit through a
/// lua request probe (same pattern as bm-subtitle-probe.json) so the exit-rewrite path - the
/// mechanism the repair branch exists for - is actually exercised and measured.</summary>
[Collection("ProgramState")]
public class RealVlcRotationTests : IDisposable
{
    private const string VlcExe = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
    private const string QuitProbePath = @"C:\Program Files\VideoLAN\VLC\lua\http\requests\bm-rotation-quit.json";
    private const string OldPassword = "old-real-password-1";
    private const string NewPassword = "new-real-password-22";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-realvlc-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _spawned = new();
    private string VlcDirPath => Path.Combine(_dir, "vlc");
    private string VlcrcPath => Path.Combine(VlcDirPath, "vlcrc");

    public RealVlcRotationTests()
    {
        Directory.CreateDirectory(VlcDirPath);
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        VlcSetup.VlcDirOverride = VlcDirPath;
        Program.RedirectDataDirForTests(Path.Combine(_dir, "data"));
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        VlcRotation.ResetForTests();
        VlcRotation.BackgroundDisabledForTests = true;  // passes are driven synchronously here
        VlcSetup.ForgetCandidateForTests();
        // Deliberately no VlcRunningOverride: process detection is the real enumeration.
    }

    public void Dispose()
    {
        foreach (var p in _spawned)
            try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(); } } catch { }
        try { File.Delete(QuitProbePath); } catch { }
        VlcSetup.VlcDirOverride = null;
        VlcSetup.VlcRunningOverride = null;
        VlcSetup.ForgetCandidateForTests();
        VlcRotation.BackgroundDisabledForTests = false;
        VlcRotation.AwaitQuiescenceForTests(TimeSpan.FromSeconds(5));
        VlcRotation.ResetForTests();
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.ResetDataDirForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void WriteHealthyVlcrc(string password)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VlcrcPath)!);
        File.WriteAllLines(VlcrcPath, new[] { "extraintf=http", "http-host=0.0.0.0", "http-password=" + password });
    }

    private static string RequireFixture()
    {
        var fixture = Environment.GetEnvironmentVariable("BINGEMOTE_SUBTITLE_VIDEO");
        Assert.False(string.IsNullOrEmpty(fixture), "Real VLC test requires an explicit video fixture");
        Assert.True(File.Exists(fixture), "fixture missing: " + fixture);
        return fixture!;
    }

    /// <summary>Launch a REAL vlc.exe reading/writing the redirected vlcrc (--config), with the
    /// web interface up on 127.0.0.1. configOnly: no --extraintf/--http-password arguments, so
    /// the interface comes up from the vlcrc alone - the proof that the rotated file suffices.</summary>
    private Process StartRealVlc(int port, string password, string fixture, bool configOnly)
    {
        File.WriteAllText(QuitProbePath, "<?vlc vlc.misc.quit() print('{\"quit\":1}') ?>");
        var psi = new ProcessStartInfo(VlcExe) { UseShellExecute = false };
        foreach (var arg in new[] { "-I", "dummy", "--aout=dummy", "--vout=dummy", "--config", VlcrcPath }) psi.ArgumentList.Add(arg);
        if (!configOnly)
            foreach (var arg in new[] { "--extraintf=http", "--http-host=127.0.0.1", "--http-password=" + password }) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--http-port=" + port);
        psi.ArgumentList.Add("--loop");
        psi.ArgumentList.Add(fixture);
        var p = Process.Start(psi)!;
        _spawned.Add(p);
        return p;
    }

    private static async Task<int> VlcHttpStatus(int port, string password)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + password)));
        try { return (int)(await client.GetAsync("http://127.0.0.1:" + port + "/requests/status.json")).StatusCode; }
        catch { return -1; }
    }

    private static async Task WaitForVlcHttp(int port, string password)
    {
        for (var i = 0; i < 80; i++)
        {
            if (await VlcHttpStatus(port, password) == 200) return;
            await Task.Delay(250);
        }
        Assert.True(false, "VLC's HTTP interface never came up on port " + port);
    }

    /// <summary>Graceful exit via the lua quit probe (vlc.misc.quit()), the key=quit hotkey
    /// command as a fallback. True when VLC actually exited; the caller decides what a kill
    /// means for its assertions.</summary>
    private static async Task<bool> TryGracefulQuit(int port, string password, Process vlc)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + password)));
        try { await client.GetAsync("http://127.0.0.1:" + port + "/requests/bm-rotation-quit.json"); } catch { }
        for (var i = 0; i < 60 && !vlc.HasExited; i++) await Task.Delay(250);
        if (vlc.HasExited) return true;
        try { await client.GetAsync("http://127.0.0.1:" + port + "/requests/status.json?command=key&val=quit"); } catch { }
        for (var i = 0; i < 40 && !vlc.HasExited; i++) await Task.Delay(250);
        return vlc.HasExited;
    }

    private async Task KillIfRunning(Process vlc)
    {
        try { if (!vlc.HasExited) { vlc.Kill(true); await vlc.WaitForExitAsync(); } } catch { }
    }

    [Fact]
    public async Task RealVlcRotation_DeferralWhileRealVlcRuns_OldPasswordKeepsWorking()
    {
        Assert.True(File.Exists(VlcExe), "Real VLC test requires the choco-installed VLC");
        var fixture = RequireFixture();
        WriteHealthyVlcrc(OldPassword);
        var before = File.ReadAllBytes(VlcrcPath);
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        using var vlc = StartRealVlc(8097, OldPassword, fixture, configOnly: false);
        try
        {
            await WaitForVlcHttp(8097, OldPassword);
            var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-a").ToArray(), NewPassword);
            Assert.True(ok);
            VlcRotation.PassOnce(); // the REAL process check must hold the write
            Assert.True(VlcSetup.Pending);
            Assert.Equal(before, File.ReadAllBytes(VlcrcPath));       // vlcrc untouched while VLC runs
            Assert.Equal(200, await VlcHttpStatus(8097, OldPassword)); // an old session keeps OLD: the stated limit is real
            Assert.Equal(401, await VlcHttpStatus(8097, NewPassword));
            Console.WriteLine("REAL_VLC_ROTATION_DEFERRAL_OLD_SESSION_PASS");
        }
        finally { await KillIfRunning(vlc); }
    }

    [Fact]
    public async Task RealVlcRotation_AfterGracefulExit_LandsNewPassword_OldRejectedNewAccepted()
    {
        Assert.True(File.Exists(VlcExe), "Real VLC test requires the choco-installed VLC");
        var fixture = RequireFixture();
        WriteHealthyVlcrc(OldPassword);
        var before = File.ReadAllBytes(VlcrcPath);
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        Program.PairedPhone kept;
        lock (Program.PhonesLock) { kept = Program.Phones.First(p => p.Token == "tok-a"); }
        kept.AdoptSpent = true; // a grant already spent on OLD
        using var vlc = StartRealVlc(8098, OldPassword, fixture, configOnly: false);
        try
        {
            await WaitForVlcHttp(8098, OldPassword);
            var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), NewPassword);
            Assert.True(ok);
            VlcRotation.PassOnce();
            Assert.True(VlcSetup.Pending); // deferred while the real VLC runs

            var graceful = await TryGracefulQuit(8098, OldPassword, vlc);
            Assert.True(vlc.HasExited, "VLC did not exit (graceful=" + graceful + ")");
            // T6 measurement: does an exiting VLC rewrite vlcrc? Printed, not assumed.
            var bytesChanged = !File.ReadAllBytes(VlcrcPath).SequenceEqual(before);
            var healthyOld = VlcSetup.ReadHealthy().HealthyFor(OldPassword);
            Console.WriteLine("REAL_VLC_EXIT graceful=" + graceful + " vlcrc_bytes_changed=" + bytesChanged + " healthy_old_after_exit=" + healthyOld);

            VlcRotation.PassOnce(); // converges: writes NEW (repairing any exit rewrite), lands, re-arms the kept phone
            Assert.True(VlcSetup.ReadHealthy().HealthyFor(NewPassword));
            Assert.False(kept.AdoptSpent); // the landing edge re-armed the kept phone exactly once
            VlcRotation.PassOnce();        // quiet healthy verify clears the target
            Assert.Null(VlcRotation.TargetValue);
            Assert.False(VlcSetup.Pending);
            Console.WriteLine("REAL_VLC_ROTATION_LANDING_PASS");
        }
        finally { await KillIfRunning(vlc); }

        // End-to-end auth proof: a fresh VLC whose ONLY source for the interface and the
        // password is the rotated vlcrc accepts NEW and rejects OLD.
        using var vlc2 = StartRealVlc(8098, NewPassword, fixture, configOnly: true);
        try
        {
            await WaitForVlcHttp(8098, NewPassword);   // 200 with NEW, served from vlcrc alone
            Assert.Equal(401, await VlcHttpStatus(8098, OldPassword));
            Console.WriteLine("REAL_VLC_ROTATION_NEW_ACCEPTED_OLD_REJECTED_PASS");
        }
        finally { await KillIfRunning(vlc2); }
    }
}
