using System.Text.Json;
using ZephyrVlcRemote;
using Xunit;

namespace ZephyrVlcRemote.Tests;

/// <summary>The rotation state machine: landing, deferral, exit-rewrite repair, grant re-arm on
/// the landing edge, persist-failure rollback, ABA abandonment, convergence and migration.
/// Source-only: these assert the mechanics, not a real VLC; runtime T1-T6 stay on a Windows+VLC route.</summary>
[Collection("ProgramState")]
public class VlcRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-rot-" + Guid.NewGuid().ToString("N"));
    private string VlcDirPath => Path.Combine(_dir, "vlc");
    private string VlcrcPath => Path.Combine(VlcDirPath, "vlcrc");

    public VlcRotationTests()
    {
        Directory.CreateDirectory(VlcDirPath);
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        VlcSetup.VlcDirOverride = VlcDirPath;
        Program.RedirectDataDirForTests(Path.Combine(_dir, "data"));
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        VlcRotation.ResetForTests();
        VlcSetup.ForgetCandidateForTests();
        VlcSetup.VlcRunningOverride = () => false;
    }

    public void Dispose()
    {
        VlcSetup.VlcDirOverride = null;
        VlcSetup.VlcRunningOverride = null;
        VlcSetup.ForgetCandidateForTests();
        VlcRotation.ResetForTests();
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        Program.ResetDataDirForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void WriteVlcrc(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VlcrcPath)!);
        File.WriteAllLines(VlcrcPath, lines);
    }

    private static Program.PairedPhone Phone(string token)
    {
        lock (Program.PhonesLock) { return Program.Phones.First(p => p.Token == token); }
    }

    [Fact]
    public void RemovalLandsWhenVlcClosed_AndTheVerifyPassClearsTheTarget()
    {
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all, "new-pass-123456");
        Assert.True(ok);
        Assert.Equal(0, Program.PhonesCount);
        Assert.True(VlcSetup.Pending);
        VlcRotation.PassOnce();
        Assert.Equal("new-pass-123456", VlcSetup.ReadVlcrcValue("http-password"));
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-pass-123456"));
        Assert.False(VlcSetup.Pending);          // landed: pending is exactly "a write outstanding"
        VlcRotation.PassOnce();                  // a quiet healthy read verifies and clears
        Assert.Null(VlcRotation.TargetValue);
        Assert.False(VlcSetup.Pending);
    }

    [Fact]
    public void DeferralWhileVlcRunning_ThenLandingOnceClosed()
    {
        VlcSetup.VlcRunningOverride = () => true;
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all, "new-pass-123456");
        Assert.True(ok);
        VlcRotation.PassOnce();
        Assert.True(VlcSetup.Pending);           // a running VLC holds the write
        Assert.False(File.Exists(VlcrcPath));
        VlcSetup.VlcRunningOverride = () => false;
        VlcRotation.PassOnce();
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-pass-123456"));
    }

    [Fact]
    public void ExitRewriteRepair_ReArmsASpentGrant()
    {
        // vlcrc healthy on OLD; the kept phone already spent its one-shot grant on OLD.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var kept = Phone("tok-a");
        kept.AdoptSpent = true;
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                  // lands NEW: the landing edge re-arms the kept phone once
        Assert.False(kept.AdoptSpent);
        kept.AdoptSpent = true;                  // it fetched NEW and spent the grant
        // An old-password VLC exits and rewrites vlcrc back to OLD; the repair pass must put NEW
        // back AND re-arm again (the clobber is a new divergence edge).
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        VlcRotation.PassOnce();
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        Assert.False(kept.AdoptSpent);
        Assert.True(VlcSetup.Pending);           // repaired but not yet verified
        VlcRotation.PassOnce();                  // quiet healthy read: verify clears the target
        Assert.Null(VlcRotation.TargetValue);
    }

    [Fact]
    public void DivergenceRecordedDuringDeferral_ThenRepairReArms()
    {
        // MUST1: the marker must move even while a running VLC blocks the write, or the later
        // landing sees no edge and a kept phone that spent its grant on OLD never re-arms.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var kept = Phone("tok-a");
        kept.AdoptSpent = true;                  // spent on OLD before the removal
        VlcSetup.VlcRunningOverride = () => true;
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                  // defers; divergence OLD is still recorded
        Assert.True(VlcSetup.Pending);
        Assert.True(kept.AdoptSpent);            // no landing yet, no re-arm yet
        VlcSetup.VlcRunningOverride = () => false;
        VlcRotation.PassOnce();                  // lands: the recorded edge re-arms the kept phone
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        Assert.False(kept.AdoptSpent);
    }

    [Fact]
    public void FailedSaveLeavesMarkerAndGrants_RetryReArms()
    {
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var kept = Phone("tok-a");
        kept.AdoptSpent = true;
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => Array.Empty<Program.PairedPhone>(), "new-password-22");
        Assert.True(ok);
        Program.StoreBroken = true;              // every save now refuses
        VlcRotation.PassOnce();                  // vlcrc written, readback healthy, persist refused
        Assert.True(kept.AdoptSpent);            // the grant mutation rolled back
        Assert.True(VlcSetup.Pending);           // not landed; the edge marker was never advanced
        Program.StoreBroken = false;
        VlcRotation.PassOnce();                  // healthy-by-observation: the persist lands and re-arms
        Assert.False(kept.AdoptSpent);
        Assert.True(VlcRotation.Landed);
    }

    [Fact]
    public void SameValueReSetBumpsGeneration_AndAStalePersistAbandons()
    {
        VlcSetup.ApplyPlannedPassword("pass-aaaa-111111");   // stages the target (setup exception)
        var (t, landed, gen) = VlcRotation.Snapshot();
        Assert.Equal("pass-aaaa-111111", t);
        using var doc = JsonDocument.Parse(VlcSetup.SetupFromPhone("pass-aaaa-111111")); // same value, still a re-set
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(VlcRotation.Generation > gen);           // ABA: identical value, newer generation
        // A pass holding the OLD snapshot abandons silently instead of clobbering the newer state.
        Assert.False(VlcRotation.PersistFromPass(t!, landed, gen, setLanded: true, clearTarget: false, t!));
    }

    [Fact]
    public void TwoRemovalsConvergeOnTheNewestPassword()
    {
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var (ok1, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-a").ToArray(), "pass-1111-aaaa");
        Assert.True(ok1);
        VlcRotation.PassOnce();
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("pass-1111-aaaa"));
        var (ok2, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "pass-2222-bbbb");
        Assert.True(ok2);
        VlcRotation.PassOnce();
        Assert.Equal("pass-2222-bbbb", VlcSetup.ReadVlcrcValue("http-password"));
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("pass-2222-bbbb"));
    }

    [Fact]
    public void LegacyPendingMigratesToTheRotationTarget_AndTheLegacyKeyVanishes()
    {
        var wrapped = Dpapi.Wrap("legacy-pass-1234");
        File.WriteAllText(Program.ConfigPath, "{\"phones\":[],\"pendingVlcPassword\":\"" + wrapped + "\"}");
        Program.LoadConfig();
        Assert.Equal("legacy-pass-1234", VlcRotation.TargetValue);
        Assert.False(VlcRotation.Landed);                    // migrated as unlanded: it converges again
        Assert.Equal(1, VlcRotation.Generation);
        Program.SaveConfig();
        var saved = File.ReadAllText(Program.ConfigPath);
        Assert.DoesNotContain("pendingVlcPassword", saved);  // the legacy key is gone from the next save
        Assert.Contains("vlcTarget", saved);
    }

    [Fact]
    public void PasswordEqualityAloneNeverLands_LoopbackBindIsRewritten()
    {
        var pw = "t-123456789012";
        WriteVlcrc("extraintf=http", "http-host=127.0.0.1", "http-password=" + pw); // loopback: NOT healthy
        Assert.False(VlcSetup.ReadHealthy().HealthyFor(pw));
        using var doc = JsonDocument.Parse(VlcSetup.SetupFromPhone(pw));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());   // the pass rewrote the full config
        Assert.True(VlcSetup.ReadHealthy().HealthyFor(pw));
        Assert.Equal("0.0.0.0", VlcSetup.ReadVlcrcValue("http-host"));
    }

    [Fact]
    public void EnumerationFailureDefersWithoutCrashing_AndReleasesTheLocks()
    {
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all, "new-pass-123456");
        Assert.True(ok);
        VlcSetup.VlcRunningOverride = () => throw new InvalidOperationException("enumeration failed");
        VlcRotation.PassOnce();                              // must not throw out of the pass
        Assert.True(VlcSetup.Pending);                       // deferred, marker kept
        Assert.True(Monitor.TryEnter(Program.ConfigSaveLock, 200));
        Monitor.Exit(Program.ConfigSaveLock);
        Assert.True(Monitor.TryEnter(VlcRotation.LaunchWriteMutex, 200));
        Monitor.Exit(VlcRotation.LaunchWriteMutex);
        VlcSetup.VlcRunningOverride = () => false;
        VlcRotation.PassOnce();                              // the retry converges
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-pass-123456"));
    }

    [Fact]
    public void PairAndAdoptAreReadOnlyMidDeferral()
    {
        // R1: with a NEW target waiting on a running VLC, pair and adopt must serve the OLD
        // vlcrc password and never replace the staged NEW target.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        VlcSetup.VlcRunningOverride = () => true;
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                              // defers
        Assert.Equal("new-password-22", VlcRotation.TargetValue);
        Assert.Equal("old-password-1", VlcSetup.PlanPassword());
        VlcSetup.ApplyPlannedPassword("old-password-1");     // read-only: vlcrc has a password
        Assert.Equal("new-password-22", VlcRotation.TargetValue);
        Assert.Equal("old-password-1", VlcSetup.ServeForAdoptInMemory());
        Assert.Equal("new-password-22", VlcRotation.TargetValue);
        Assert.Equal("old-password-1", VlcSetup.EffectivePassword());
    }

    [Fact]
    public void SetupExceptionServeStagesTheTarget_AndThePassLandsIt()
    {
        // No vlcrc at all: adopt serving plans the shared candidate and stages it as the target.
        var served = VlcSetup.ServeForAdoptInMemory();
        Assert.Equal(24, served.Length);
        Assert.Equal(served, VlcRotation.TargetValue);
        Assert.True(VlcSetup.Pending);
        VlcRotation.PassOnce();
        Assert.Equal(served, VlcSetup.ReadVlcrcValue("http-password"));
        Assert.True(VlcSetup.ReadHealthy().HealthyFor(served));
    }
}
