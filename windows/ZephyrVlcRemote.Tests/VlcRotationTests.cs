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
        VlcRotation.BackgroundDisabledForTests = true;  // no pool pass may leak into the next test
        VlcSetup.ForgetCandidateForTests();
        VlcSetup.VlcRunningOverride = () => false;
    }

    public void Dispose()
    {
        VlcRotation.BackgroundDisabledForTests = false;
        VlcRotation.AwaitQuiescenceForTests(TimeSpan.FromSeconds(5));  // drain anything already in flight
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
        Assert.True(VlcRotation.Landed);           // the repair wrote + read back + persisted: landed holds
        Assert.False(VlcSetup.Pending);            // pending is exactly "a write outstanding" - none is
        VlcRotation.PassOnce();                    // quiet healthy read: verify clears the target
        Assert.Null(VlcRotation.TargetValue);
    }

    [Fact]
    public void DivergenceWhileLanded_ReopensThePendingStateUntilRepaired()
    {
        // After the landing, an old-password VLC clobbers vlcrc back to OLD and STAYS OPEN: the
        // write cannot run, and the published state must stop claiming completion - the rotation
        // re-opens (pending) until the repair lands, so no window or dialog says "changed" while
        // the disk holds OLD. Grants and the marker must NOT move on the observation: the re-arm
        // belongs to the re-landing.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var kept = Phone("tok-a");
        var (ok, gen) = Program.RemovePhonesAndRotatePersist(all => Array.Empty<Program.PairedPhone>(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                              // lands NEW
        Assert.True(VlcRotation.Landed);
        Assert.True(VlcRotation.RotationOutcomeComplete(gen));
        kept.AdoptSpent = true;                              // the kept phone fetched NEW and spent the grant
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1"); // the clobber
        VlcSetup.VlcRunningOverride = () => true;            // the old VLC stays open: no write allowed
        VlcRotation.PassOnce();
        Assert.False(VlcRotation.Landed);                    // re-opened: the disk no longer matches the target
        Assert.True(VlcSetup.Pending);                       // the window note shows the truth
        Assert.False(VlcRotation.RotationOutcomeComplete(gen)); // no false completion while the disk holds OLD
        Assert.True(kept.AdoptSpent);                        // observation alone does not re-arm
        VlcSetup.VlcRunningOverride = () => false;
        VlcRotation.PassOnce();                              // repairs and re-lands (the landing edge re-arms)
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        Assert.False(kept.AdoptSpent);                       // re-armed exactly at the re-landing
        Assert.True(VlcRotation.RotationOutcomeComplete(gen));
        kept.AdoptSpent = true;                              // fetched NEW again and spent
        VlcRotation.PassOnce();                              // quiet verify clears - and must NOT re-arm again
        Assert.Null(VlcRotation.TargetValue);
        Assert.True(kept.AdoptSpent);                        // exactly one re-arm for the whole clobber cycle
    }

    [Fact]
    public void StalePassNeverWritesASupersededTarget()
    {
        // The atomic staging/write ordering, exercised as the real race: a pass snapshots T1 and
        // blocks on LaunchWriteMutex (held here by the test thread - the same exclusion Play's
        // multi-second VLC launch holds); a SECOND removal stages T2 from under that same mutex
        // (re-entrant on this thread, which is exactly what proves staging is mutex-ordered now);
        // the released pass must refuse its stale snapshot and write NOTHING - vlcrc keeps the old
        // password until T2 itself lands. Pre-fix, the pass wrote T1 and only its persist abandoned.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var (ok1, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token != "tok-a").ToArray(), "new-password-1");
        Assert.True(ok1);                                        // T1 staged (unlanded)
        var snapshotted = new ManualResetEventSlim();
        VlcRotation.AfterSnapshotForTests = () => snapshotted.Set();
        Thread passThread;
        lock (VlcRotation.LaunchWriteMutex)                      // the pass blocks on this AFTER its snapshot
        {
            passThread = new Thread(() => VlcRotation.PassOnce());
            passThread.Start();
            Assert.True(snapshotted.Wait(TimeSpan.FromSeconds(10)), "the pass never reached its snapshot");
            var (ok2, _) = Program.RemovePhonesAndRotatePersist(all => Array.Empty<Program.PairedPhone>(), "new-password-2");
            Assert.True(ok2);                                    // T2 staged WHILE the pass waits: mutex-ordered
        }
        Assert.True(passThread.Join(TimeSpan.FromSeconds(10)), "the pass never finished");
        Assert.True(VlcRotation.AwaitQuiescenceForTests(TimeSpan.FromSeconds(5)));
        Assert.Equal("old-password-1", VlcSetup.ReadVlcrcValue("http-password")); // T1 was NEVER written
        Assert.True(VlcSetup.Pending);                           // T2 is outstanding, truthfully
        VlcRotation.PassOnce();                                  // the kicked convergence: T2 itself lands
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-2"));
        Assert.False(VlcSetup.Pending);
    }

    [Fact]
    public void InlineAttemptOnDivergedLandedReopensBeforeDeferring()
    {
        // The re-open lives in the ONE shared write path, so an INLINE attempt (the /vlc/setup
        // and Movies.Play route, driven here through TryInlinePass) that observes divergence
        // while landed also re-opens FIRST: even though the running VLC forces a deferral, the
        // published state stops claiming completion immediately rather than waiting for a timer pass.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var (ok, gen) = Program.RemovePhonesAndRotatePersist(all => Array.Empty<Program.PairedPhone>(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                              // lands NEW
        Assert.True(VlcRotation.Landed);
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1"); // the clobber
        VlcSetup.VlcRunningOverride = () => true;
        var outcome = VlcRotation.TryInlinePass();           // the inline seam itself
        Assert.Equal(VlcRotation.ApplyOutcome.Deferred, outcome); // the write waits for the running VLC
        Assert.True(VlcSetup.Pending);                       // ...but the re-open already persisted
        Assert.False(VlcRotation.RotationOutcomeComplete(gen));
        Assert.Equal("old-password-1", VlcSetup.ReadVlcrcValue("http-password")); // no write attempted
        VlcSetup.VlcRunningOverride = () => false;
        VlcRotation.PassOnce();                              // repairs and re-lands
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        Assert.True(VlcRotation.RotationOutcomeComplete(gen));
    }

    [Fact]
    public void ReOpenPersistFailureAbortsTheRepairAndKeepsStateConsistent()
    {
        // If the re-open persist is refused, the rollback restores landed and the repair must
        // NOT run: a vlcrc write whose own persist abandons would leave disk NEW with spent OLD
        // grants. State stays consistent and the next pass retries the re-open.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => Array.Empty<Program.PairedPhone>(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                              // lands NEW
        Assert.True(VlcRotation.Landed);
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1"); // the clobber
        Program.StoreBroken = true;
        VlcRotation.PassOnce();                              // re-open persist refused: rollback, NO repair write
        Assert.True(VlcRotation.Landed);                     // not half-re-opened
        Assert.Equal("old-password-1", VlcSetup.ReadVlcrcValue("http-password")); // no write was attempted
        Program.StoreBroken = false;
        VlcRotation.PassOnce();                              // retries: re-opens, repairs, re-lands
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        Assert.True(VlcRotation.Landed);
    }

    [Fact]
    public void StaleSetupValueNeverReplacesALandedRotationTarget()
    {
        // /vlc/setup with a stale password (a kept phone's late Finish carrying OLD) must not
        // stage over the revocation: target, generation and vlcrc stay NEW, and the reply is a
        // non-ok "password changed" so the phone re-fetches the current password through adopt.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var (ok, gen) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                              // lands NEW
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        using var doc = JsonDocument.Parse(VlcSetup.SetupFromPhone("old-password-1"));   // the stale value
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("password changed", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal("new-password-22", VlcRotation.TargetValue);   // untouched
        Assert.Equal(gen, VlcRotation.Generation);                  // no bump
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22")); // disk untouched
    }

    [Fact]
    public void StaleSetupValueNeverReplacesADeferredRotationTarget()
    {
        // Same guard mid-deferral: VLC open, NEW staged but not landed, vlcrc still OLD. A setup
        // call carrying a THIRD value must not replace the outstanding NEW target.
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=old-password-1");
        VlcSetup.VlcRunningOverride = () => true;
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var (ok, gen) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                              // defers: NEW staged, vlcrc still OLD
        Assert.Equal("new-password-22", VlcRotation.TargetValue);
        using var doc = JsonDocument.Parse(VlcSetup.SetupFromPhone("some-other-value-1"));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("new-password-22", VlcRotation.TargetValue);
        Assert.Equal(gen, VlcRotation.Generation);
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("old-password-1")); // disk untouched
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

    [Fact]
    public void UncommittedTargetIsNeverWrittenByAConcurrentPass()
    {
        // D1: a pass must observe only committed state. Stage a target WITHOUT saving (a writer
        // mid-transaction holds ConfigSaveLock until SaveConfig succeeds): the concurrent pass
        // must block on the save lock and write nothing. A save that then fails rolls the target
        // back, and vlcrc must never have carried the uncommitted value.
        var backup = VlcRotation.BackupTarget();
        lock (Program.ConfigSaveLock)
        {
            VlcRotation.SetTargetUnderConfigSaveLock("uncommitted-123456");
            var pass = Task.Run(() => VlcRotation.PassOnce());
            Thread.Sleep(300);                                 // generous window for a buggy pass to write
            Assert.False(pass.IsCompleted);                    // still waiting on the save lock
            Assert.False(File.Exists(VlcrcPath));              // and nothing on disk
            VlcRotation.RestoreTargetUnderConfigSaveLock(backup); // the save "failed": rollback
        }
        Assert.True(VlcRotation.AwaitQuiescenceForTests(TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);                                     // let the released pass finish
        Assert.True(VlcRotation.AwaitQuiescenceForTests(TimeSpan.FromSeconds(5)));
        Assert.Null(VlcRotation.TargetValue);                  // rolled back
        Assert.False(File.Exists(VlcrcPath));                  // the uncommitted value never landed
    }

    [Fact]
    public void ApplyPlannedPasswordNeverOverwritesALiveTarget()
    {
        // D2: a value planned from an OLD vlcrc (or no vlcrc) must never replace a NEW rotation
        // target staged by a later removal.
        var planned = VlcSetup.PlanPassword();                 // plans a candidate from an empty vlcrc
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "new-password-22");
        Assert.True(ok);
        Assert.Equal("new-password-22", VlcRotation.TargetValue);
        VlcSetup.ApplyPlannedPassword(planned);                // stale plan arrives late
        Assert.Equal("new-password-22", VlcRotation.TargetValue);   // the live target survives
    }

    [Fact]
    public void ReadGlitchDoesNotMoveTheMarker_OrReArmAGrant()
    {
        // A transient vlcrc read failure is unknown, not divergence: the marker must stay put,
        // and the next healthy read must NOT re-arm a grant for a value that never diverged.
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var kept = Phone("tok-a");
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => Array.Empty<Program.PairedPhone>(), "new-password-22");
        Assert.True(ok);
        VlcRotation.PassOnce();                              // lands NEW
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        kept.AdoptSpent = true;                              // the kept phone fetched NEW and spent the grant
        using (var fs = new FileStream(VlcrcPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            VlcRotation.PassOnce();                          // read denied: unknown, no divergence, no re-arm
            Assert.True(kept.AdoptSpent);
            Assert.Equal("new-password-22", VlcRotation.TargetValue); // the marker kept the target
        }
        VlcRotation.PassOnce();                              // healthy read: verify clears, still no spurious re-arm
        Assert.True(kept.AdoptSpent);
        Assert.Null(VlcRotation.TargetValue);
    }

    [Fact]
    public void BackgroundKickIsDisabledInTests_AndTheSynchronousPassStillConverges()
    {
        // The fixture disables Kick's pool pass: staging alone must write NOTHING even after a
        // wait, and the explicit synchronous pass does the work. Proves no background pass can
        // leak across tests, and that the disable seam itself works.
        var pw = "bg-disabled-12345678";
        VlcSetup.ApplyPlannedPassword(pw);                   // stages + kicks (kick is a no-op here)
        Assert.Equal(pw, VlcRotation.TargetValue);
        Thread.Sleep(250);
        Assert.False(File.Exists(VlcrcPath));                // nothing ran in the background
        Assert.True(VlcRotation.AwaitQuiescenceForTests(TimeSpan.FromSeconds(5)));
        VlcRotation.PassOnce();
        Assert.True(VlcSetup.ReadHealthy().HealthyFor(pw));
    }

    [Fact]
    public void OutcomeCompletenessDistinguishesPendingLandedAndCleared()
    {
        // The verify pass clears the target WITHOUT a generation bump: a snapshot taken after a
        // fully successful rotation reads (no target, unlanded, same generation), and the removal
        // UI must still read that as complete, never as "not changed yet".
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        var (ok, gen) = Program.RemovePhonesAndRotatePersist(all => all, "new-pass-123456");
        Assert.True(ok);
        Assert.False(VlcRotation.RotationOutcomeComplete(gen));   // staged, not landed
        VlcRotation.PassOnce();                                    // lands
        Assert.True(VlcRotation.RotationOutcomeComplete(gen));     // landed
        VlcRotation.PassOnce();                                    // verify clears the target, gen unchanged
        Assert.Null(VlcRotation.TargetValue);
        Assert.True(VlcRotation.RotationOutcomeComplete(gen));     // cleared-after-success is still complete
        Assert.False(VlcRotation.RotationOutcomeComplete(gen + 1));// a newer removal's generation is not
    }

    [Fact]
    public void CrashAfterWriteBeforePersist_RestartPassCountsTheFreshUnlandedObservationAsLanding()
    {
        // I2: the pass writes vlcrc NEW and dies before the landing persist. On restart the
        // marker is empty (nothing observed this process) but the persisted target is UNLANDED:
        // the healthy read must count as the landing, or a kept phone that spent its grant on
        // OLD is never re-armed and can never fetch NEW.
        Program.AddPhoneAndPersist("tok-a", "Phone A");
        Program.AddPhoneAndPersist("tok-b", "Phone B");
        var kept = Phone("tok-a");
        kept.AdoptSpent = true;                              // spent on OLD before the removal
        var (ok, _) = Program.RemovePhonesAndRotatePersist(all => all.Where(x => x.Token == "tok-b").ToArray(), "new-password-22");
        Assert.True(ok);
        Program.StoreBroken = true;
        VlcRotation.PassOnce();                              // vlcrc written NEW; the landing persist is refused
        Assert.True(VlcSetup.ReadHealthy().HealthyFor("new-password-22"));
        Assert.True(kept.AdoptSpent);                        // no landing committed, no re-arm yet
        // Simulate the restart: in-memory state gone, the persisted target restored as UNLANDED.
        var (t, landed, gen) = VlcRotation.Snapshot();
        Assert.False(landed);
        VlcRotation.ResetForTests();                         // clears the in-memory marker (_hasObserved)
        VlcRotation.LoadPersisted(t, landed, gen);
        Program.StoreBroken = false;
        VlcRotation.PassOnce();                              // first pass of the "new process": nothing observed yet
        Assert.False(kept.AdoptSpent);                       // the fresh unlanded observation counted as the landing
        Assert.True(VlcRotation.Landed);
        VlcRotation.PassOnce();                              // quiet healthy verify clears the target
        Assert.Null(VlcRotation.TargetValue);
        Assert.False(kept.AdoptSpent);                       // and the verify pass does not flip it back
    }
}
