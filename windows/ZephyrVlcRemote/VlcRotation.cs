// Phone-removal rotation: ONE credential in use, a landed/verify state machine, one apply
// pass. vlcTarget is the credential the helper is making APPLIED; vlcLanded=false is the
// only published "pending"; every vlcrc write goes through the pass's atomic temp+rename
// (single writer); kept phones' one-shot adopt grants re-arm exactly once per landing edge.
// The password value is never logged.

namespace ZephyrVlcRemote;

internal static class VlcRotation
{
    internal enum ApplyOutcome { NoTarget, Landed, Deferred, WriteFailed }

    private static readonly object Gate = new();
    private static string? _target;        // persisted (dpapi-wrapped in config.json)
    private static bool _landed;           // persisted: vlcrc confirmed healthy-for-target at least once
    private static int _generation;        // persisted; EVERY writer bumps it, so an identical-value re-set (ABA) still abandons a stale pass
    private static string? _lastObserved;  // in-memory only: landing-edge detection
    private static bool _hasObserved;      // distinguishes "never observed" from "observed no password"
    private static int _applyBusy;         // try-enter single-flight guard (Interlocked), never nested inside Gate/ConfigSaveLock
    private static int _rerunRequested;    // a kick dropped by an active pass is not lost
    private static System.Threading.Timer? _timer;
    private static TaskCompletionSource _passDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static long _passStarts;    // incremented when a pass acquires applyBusy
    private static long _passDoneUpTo;  // start id of the latest completed pass

    // Test seam: Kick normally queues a background pool pass, which can outlive a test's Dispose
    // and land in the NEXT test's static state. Fixtures disable the background and drive PassOnce
    // synchronously; AwaitQuiescenceForTests drains anything already in flight.
    private static int _backgroundDisabled;
    internal static bool BackgroundDisabledForTests
    {
        get => Volatile.Read(ref _backgroundDisabled) == 1;
        set => Volatile.Write(ref _backgroundDisabled, value ? 1 : 0);
    }

    /// <summary>Passes started so far. A caller captures this BEFORE a kick, then waits for a
    /// completion newer than it: a completion of a pass that was already in flight at the kick
    /// says nothing about the kicked work.</summary>
    internal static long PassStartCount => Interlocked.Read(ref _passStarts);

    /// <summary>Test seam: fires in the landed verify branch AFTER the no-VLC-running check and
    /// BEFORE the fresh vlcrc read the clear is decided from - the latest moment an old-VLC exit
    /// rewrite could land. Pins the ordering: the clear's read must come after the quiet check.
    /// Null in production.</summary>
    internal static Action? BeforeVerifyReadForTests;

    /// <summary>Test seam: fires right before PassCore takes the LaunchWriteMutex, after its
    /// obs read - so a fixture can land an old-VLC exit rewrite in the exact window between the
    /// pre-mutex observation and the in-mutex verify/apply decisions. Null in production.</summary>
    internal static Action? BeforeMutexForTests;

    /// <summary>Test seam: fires right after PassCore's snapshot, so a fixture can hold the
    /// LaunchWriteMutex and stage a NEWER target between a pass's snapshot and its write - the
    /// exact stale-snapshot race the in-mutex recheck closes. Null in production.</summary>
    internal static Action? AfterSnapshotForTests;

    /// <summary>Wait until no pass is running and none is queued; false on timeout. Tests call
    /// this in Dispose so no pool pass leaks into the next test's static state. Queued pool items
    /// only exist if a test queued them itself: the background is disabled in fixtures, and this
    /// drains passes that already hold applyBusy or a flagged rerun, not never-started items.</summary>
    internal static bool AwaitQuiescenceForTests(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Volatile.Read(ref _applyBusy) != 0 || Volatile.Read(ref _rerunRequested) != 0)
        {
            if (Environment.TickCount64 > deadline) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    /// <summary>Launch/write exclusion shared with Movies.Play. Lock order everywhere:
    /// applyBusy &gt; LaunchWriteMutex &gt; ConfigSaveLock &gt; Gate (PhonesLock inside ConfigSaveLock).</summary>
    internal static readonly object LaunchWriteMutex = new();

    // Persisted surface: read by Program.SaveConfig; mutated only inside ConfigSaveLock persists.
    internal static string? TargetValue { get { lock (Gate) return _target; } }
    internal static bool Landed { get { lock (Gate) return _landed; } }
    internal static int Generation { get { lock (Gate) return _generation; } }
    /// <summary>Published pending: exactly "a write is genuinely outstanding" (target set, not yet landed).</summary>
    internal static bool PendingPublished { get { lock (Gate) return _target != null && !_landed; } }
    internal static (string? t, bool landed, int gen) Snapshot() { lock (Gate) return (_target, _landed, _generation); }

    /// <summary>The only snapshot a WRITER may use: taken under the save lock, so a pass can
    /// never observe a target whose transaction has not committed. Without this, a pass reading
    /// between a removal's SetTarget and its SaveConfig could write vlcrc from a target that a
    /// failed save then rolls back - and "still paired, nothing changed" would be a lie on disk.</summary>
    internal static (string? t, bool landed, int gen) SnapshotCommitted() { lock (Program.ConfigSaveLock) return Snapshot(); }

    internal enum RemovalOutcome { NotYet, Done, Superseded }

    /// <summary>The removal dialog's call, from ONE committed snapshot plus a disk read bound to that
    /// snapshot's target. Landed alone never says done: the rollback of a refused re-open persist
    /// restores landed while vlcrc still holds OLD, so done requires the disk to actually show the
    /// target - or the verified clear, which has no target left to check. A diverged OR unreadable
    /// disk is not-yet: under-claiming during a read glitch is the safe side, and the retry is armed.</summary>
    internal static RemovalOutcome ClassifyRemovalOutcome(string? target, bool landed, int snapshotGen, int removalGen, bool diskHealthy)
    {
        if (snapshotGen != removalGen) return RemovalOutcome.Superseded;
        if (target == null) return RemovalOutcome.Done;      // verified and cleared: state-only by design
        if (landed && diskHealthy) return RemovalOutcome.Done;
        return RemovalOutcome.NotYet;
    }

    /// <summary>For the removal-outcome UI: this removal's generation is complete once its pass
    /// landed - OR once the verify pass already cleared the target. The clear does NOT bump the
    /// generation, so a fully successful rotation can read back as (no target, unlanded, same
    /// generation); reading only `landed` there would falsely report "not changed yet".</summary>
    internal static bool RotationOutcomeComplete(int removalGen)
    {
        var (t, landed, g) = Snapshot();
        return g == removalGen && (landed || t == null);
    }

    /// <summary>Startup restore (including the legacy-pending migration). The in-memory marker stays null.</summary>
    internal static void LoadPersisted(string? target, bool landed, int gen) { lock (Gate) { _target = target; _landed = landed; _generation = gen; } }
    internal static void ResetForTests()
    {
        lock (Gate) { _target = null; _landed = false; _generation = 0; _lastObserved = null; _hasObserved = false; }
        Interlocked.Exchange(ref _applyBusy, 0);
        Interlocked.Exchange(ref _rerunRequested, 0);
        AfterSnapshotForTests = null;
        BeforeMutexForTests = null;
        BeforeVerifyReadForTests = null;
    }

    // Target mutation: only ever inside a ConfigSaveLock persist (the single-writer invariant).
    internal static (string? t, bool landed, int gen) BackupTarget() => Snapshot();
    internal static void SetTargetUnderConfigSaveLock(string t) { lock (Gate) { _target = t; _landed = false; _generation++; } }
    /// <summary>Re-open a landed rotation after a pass OBSERVED divergence: the disk no longer
    /// matches the target, so the persisted state must stop claiming completion (landed stays
    /// true is a lie while vlcrc holds the old password). Target and generation are untouched
    /// (the repair persist still compares this generation), grants and the marker are untouched
    /// (re-arming belongs to the re-landing, not to the observation). False when the snapshot
    /// moved on or the state is already unlanded.</summary>
    internal static bool MarkUnlandedUnderConfigSaveLock(string expectT, int expectGen)
    {
        lock (Gate)
        {
            if (_target != expectT || _generation != expectGen || !_landed) return false;
            _landed = false;
            return true;
        }
    }
    internal static void RestoreTargetUnderConfigSaveLock((string? t, bool landed, int gen) b) { lock (Gate) { _target = b.t; _landed = b.landed; _generation = b.gen; } }

    /// <summary>The app-owned 15 s timer (a System.Threading.Timer: its callback already runs on a
    /// pool thread, never the UI thread) plus the startup kick. Independent of any window.</summary>
    internal static void Start()
    {
        if (_timer == null) _timer = new System.Threading.Timer(_ => Kick(), null, 15000, 15000);
        Kick(); // a restored or migrated target begins converging now
    }

    /// <summary>Best-effort: GUARANTEED convergence comes from the timer; a kick dropped by an
    /// active pass only arms the rerun flag, consumed after applyBusy is released.</summary>
    internal static void Kick()
    {
        if (BackgroundDisabledForTests) return; // tests drive PassOnce synchronously
        Interlocked.Exchange(ref _rerunRequested, 1);
        ThreadPool.QueueUserWorkItem(_ => PassOnce());
    }

    private static void SignalPassDone()
    {
        _passDone.TrySetResult();
        _passDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The removal path's async join: completes when the next pass finishes (or on timeout);
    /// the caller then chooses its copy from the persisted state, never from whichever pass ran.</summary>
    internal static async Task AwaitNextPassCompletion(long afterStart, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Interlocked.Read(ref _passDoneUpTo) > afterStart) return; // a pass that STARTED after the kick finished
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;                       // no newer pass inside the window: read the state as-is
            var t = _passDone.Task;                                       // re-captured each round: a stale completion swaps it
            await Task.WhenAny(t, Task.Delay(remaining));                 // a stale completion (older pass) loops, not returns
        }
    }

    /// <summary>One pass on a worker: single-flight, broad catch (no secrets in the log), finally releases.</summary>
    internal static void PassOnce()
    {
        if (Interlocked.CompareExchange(ref _applyBusy, 1, 0) != 0) return;
        var myStart = Interlocked.Increment(ref _passStarts);
        try { PassCore(); }
        catch (Exception ex) { Program.Log("VLC password-change pass failed (" + ex.GetType().Name + ")."); }
        finally
        {
            Interlocked.Exchange(ref _applyBusy, 0);
            Interlocked.Exchange(ref _passDoneUpTo, myStart);
            SignalPassDone();
            if (Interlocked.Exchange(ref _rerunRequested, 0) == 1 && !BackgroundDisabledForTests) ThreadPool.QueueUserWorkItem(_ => PassOnce());
        }
    }

    private static void PassCore()
    {
        var (t, landed, gen) = SnapshotCommitted(); // never a staged-but-unsaved target
        AfterSnapshotForTests?.Invoke();
        if (t == null) return;
        var obs = VlcSetup.ReadHealthy();
        if (!obs.ReadOk) return; // a read glitch is not divergence: keep the marker, retry next pass
        // Divergence is recorded BEFORE any process gate, in BOTH branches: it is observation and
        // does not depend on write permission. Otherwise an old-VLC exit clobber followed by a new
        // VLC start would leave the marker at t and the later repair pass would see no edge - and a
        // kept phone that spent its grant on OLD would never re-arm.
        if (!obs.HealthyFor(t)) RecordDivergence(obs.Password);
        BeforeMutexForTests?.Invoke();
        lock (LaunchWriteMutex)
        {
            VlcSetup.SweepTemp(); // under the mutex AND skipping fresh temps: never deletes Play's in-flight temp
            if (landed)
            {
                // Verify-clear ORDERING is the revocation: FIRST establish that no VLC process
                // exists right now, ONLY THEN take the fresh read the clear is decided from. The
                // reverse order leaves a window: an old-password VLC running at the read (disk
                // healthy) exits and rewrites OLD before the process check. A process started
                // after this check read the NEW password at startup, so no OLD rewrite can land
                // between the check and the clear. The clear is quiet-only, but a diverged disk
                // must be re-opened and PERSISTED even while VLC runs: ApplyTargetUnderMutex
                // persists the re-open before deferring the write, so no window or dialog claims
                // the change while vlcrc holds the old password.
                var running = VlcSetup.VlcRunning();
                BeforeVerifyReadForTests?.Invoke();
                var quiet = VlcSetup.ReadHealthy();
                if (!quiet.ReadOk) return; // a read glitch is not divergence: keep the marker, retry next pass
                if (quiet.HealthyFor(t))
                {
                    if (running) return; // verify waits for quiet; never clear over a live VLC
                    if (PersistFromPass(t, true, gen, setLanded: false, clearTarget: true, t))
                        Program.Log("VLC password change verified; the rotation is complete.");
                    return;
                }
                // Diverged: record (the grant re-arm edge) and repair/re-open through the one
                // shared path whether or not VLC is running - it defers the write until quiet.
                RecordDivergence(quiet.Password);
                if (ApplyTargetUnderMutex(t, landed, gen, quiet) == ApplyOutcome.Landed)
                    Program.Log("VLC password change re-applied after a VLC exit rewrote vlcrc.");
                return;
            }
            // Unlanded: obs predates the mutex; decide the repair from a fresh in-mutex read too.
            var fresh = VlcSetup.ReadHealthy();
            if (!fresh.ReadOk) return; // a read glitch is not divergence: keep the marker, retry next pass
            if (!fresh.HealthyFor(t)) RecordDivergence(fresh.Password); // the exit-rewrite edge the pre-mutex read missed
            ApplyTargetUnderMutex(t, landed, gen, fresh);
        }
    }

    /// <summary>Divergence while landed: the disk no longer matches the target, so the persisted
    /// state must stop claiming completion BEFORE any repair is attempted (a running VLC can hold
    /// the repair for hours). A persisted unlanding only: target and generation untouched (the
    /// repair persist still compares this generation), grants and marker untouched (the re-arm
    /// belongs to the re-landing). False when the state moved on or the persist failed (rolled
    /// back) - the caller must then NOT attempt a write, or vlcrc could land NEW while the grant
    /// re-arm rolls back, locking kept phones out after the verify.</summary>
    private static bool ReopenLandedUnderMutex(string t, int gen)
    {
        (string? t2, bool l2, int g2) backup = default;
        var marked = false;
        var reopened = Program.MutateAndPersist(() =>
            {
                backup = BackupTarget(); // inside the transaction
                marked = MarkUnlandedUnderConfigSaveLock(t, gen);
            },
            () => { if (marked) RestoreTargetUnderConfigSaveLock(backup); },
            "the VLC password re-open");
        return marked && reopened;
    }

    /// <summary>The shared write step (the pass, Movies.Play's inline attempt, /vlc/setup's inline
    /// attempt): a landed-but-diverged rotation is RE-OPENED first (persisted, so no caller can
    /// leave the published state claiming completion while the disk holds the old password),
    /// landed-by-observation first, defer on a process, else write the FULL healthy config
    /// + readback, then the SAME persist. The caller holds LaunchWriteMutex.</summary>
    internal static ApplyOutcome ApplyTargetUnderMutex(string t, bool landed, int gen, VlcSetup.VlcrcHealth obs)
    {
        // PassCore/TryInlinePass snapshot BEFORE the mutex, and every stage of a new target is
        // serialized under this SAME mutex - so one committed re-read here makes a superseded
        // write impossible, not just unlikely: a stage either completed before we acquired the
        // mutex (caught now) or starts after we release it (ordered strictly after this apply,
        // and its own kick converges it). Play snapshots inside the mutex; its recheck passes.
        var (tNow, _, genNow) = SnapshotCommitted();
        if (tNow != t || genNow != gen) return ApplyOutcome.WriteFailed; // moved on while we waited for the mutex
        if (!obs.ReadOk) return ApplyOutcome.WriteFailed; // never write over a vlcrc we could not read
        if (landed && !obs.HealthyFor(t))
        {
            if (!ReopenLandedUnderMutex(t, gen)) return ApplyOutcome.WriteFailed; // moved on, or persist refused and rolled back
            landed = false;
        }
        if (obs.HealthyFor(t))
        {
            return PersistFromPass(t, landed, gen, setLanded: true, clearTarget: false, t)
                ? ApplyOutcome.Landed : ApplyOutcome.WriteFailed;
        }
        if (VlcSetup.VlcRunning()) return ApplyOutcome.Deferred;
        if (VlcSetup.TryAtomicWriteHealthy(t) && VlcSetup.ReadHealthy().HealthyFor(t))
        {
            return PersistFromPass(t, landed, gen, setLanded: true, clearTarget: false, t)
                ? ApplyOutcome.Landed : ApplyOutcome.WriteFailed;
        }
        return ApplyOutcome.WriteFailed;
    }

    /// <summary>One synchronous inline attempt (/vlc/setup, tests): takes applyBusy when free; a busy
    /// guard means another pass is in flight and the timer converges (kicks are best-effort).</summary>
    internal static ApplyOutcome TryInlinePass()
    {
        if (Interlocked.CompareExchange(ref _applyBusy, 1, 0) != 0) return ApplyOutcome.Deferred;
        var myStart = Interlocked.Increment(ref _passStarts);
        try
        {
            var (t, landed, gen) = SnapshotCommitted(); // never a staged-but-unsaved target
            if (t == null) return ApplyOutcome.NoTarget;
            var obs = VlcSetup.ReadHealthy();
            if (obs.ReadOk && !obs.HealthyFor(t)) RecordDivergence(obs.Password);
            lock (LaunchWriteMutex)
            {
                // Same freshness rule as PassCore: obs predates the mutex, and an old-password
                // VLC can have exited and rewritten vlcrc in between - decide from a read taken
                // HERE. (A wrong landed marker self-corrects at the next pass and the target is
                // kept, but the persisted state must not claim a landing disk no longer shows.)
                var fresh = VlcSetup.ReadHealthy();
                if (!fresh.ReadOk) return ApplyOutcome.WriteFailed; // unreadable is unknown, never writable
                if (!fresh.HealthyFor(t)) RecordDivergence(fresh.Password);
                return ApplyTargetUnderMutex(t, landed, gen, fresh);
            }
        }
        catch (Exception ex)
        {
            Program.Log("VLC password-change pass failed (" + ex.GetType().Name + ").");
            return ApplyOutcome.WriteFailed;
        }
        finally { Interlocked.Exchange(ref _applyBusy, 0); Interlocked.Exchange(ref _passDoneUpTo, myStart); SignalPassDone(); }
    }

    /// <summary>Divergence observation (vlcrc not healthy-for-t, and the read itself succeeded):
    /// the marker tracks the observed password. _hasObserved separates "never observed" from
    /// "observed no password line", so a fresh-install landing is not mistaken for an edge.
    /// Callers gate on ReadOk: a transient read glitch must never move the marker, or the next
    /// landing would re-arm grants for a value that never actually diverged.</summary>
    internal static void RecordDivergence(string? observedPassword) { lock (Gate) { _lastObserved = observedPassword; _hasObserved = true; } }

    /// <summary>The ONE persist: compare the pass's whole snapshot (target, landed AND generation)
    /// under ConfigSaveLock; a mismatch abandons silently - a newer writer installed a newer target
    /// between the pass's read and now. The landing edge (marker != t) joins the persist's actions
    /// and the marker advances ONLY after SaveConfig succeeds, so an abandoned or failed persist
    /// leaves the edge eligible and the retry still re-arms. True when committed.</summary>
    internal static bool PersistFromPass(string expectT, bool expectLanded, int expectGen, bool setLanded, bool clearTarget, string t)
    {
        lock (Program.ConfigSaveLock)
        {
            string? priorT; bool priorLanded; int priorGen;
            bool edge;
            var priorGrants = new Dictionary<Program.PairedPhone, (bool spent, string? req, DateTime? at)>();
            lock (Gate)
            {
                if (_target != expectT || _landed != expectLanded || _generation != expectGen) return false; // abandoned
                // A restart loses the marker: when this process has observed nothing yet and the
                // target was persisted UNLANDED, the previous run wrote vlcrc and died before the
                // landing persist - the fresh unlanded observation IS the landing, or a kept phone
                // that spent its grant on OLD is never re-armed and can never fetch NEW. A landed
                // target with no observation is the normal post-landing restart: the grants were
                // already re-armed at the original landing, so no edge.
                edge = (_hasObserved && _lastObserved != t) || (!_hasObserved && !expectLanded);
                priorT = _target; priorLanded = _landed; priorGen = _generation;
                lock (Program.PhonesLock)
                {
                    foreach (var p in Program.Phones) priorGrants[p] = (p.AdoptSpent, p.AdoptRequestId, p.AdoptServedAtUtc);
                    // Re-arm on a landing edge only: kept phones get exactly one fresh grant per value per divergence.
                    if (edge) foreach (var p in Program.Phones) { p.AdoptSpent = false; p.AdoptRequestId = null; p.AdoptServedAtUtc = null; }
                }
                if (clearTarget) { _target = null; _landed = false; }
                if (setLanded) _landed = true;
            }
            try { Program.SaveConfig(); }
            catch
            {
                lock (Gate)
                {
                    _target = priorT; _landed = priorLanded; _generation = priorGen;
                    lock (Program.PhonesLock)
                        foreach (var kv in priorGrants)
                        {
                            kv.Key.AdoptSpent = kv.Value.spent;
                            kv.Key.AdoptRequestId = kv.Value.req;
                            kv.Key.AdoptServedAtUtc = kv.Value.at;
                        }
                }
                throw; // the marker was never advanced: the retry still sees the edge and re-arms
            }
            if (edge) lock (Gate) { _lastObserved = t; } // ONLY after the save succeeded
            return true;
        }
    }
}
