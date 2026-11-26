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

    // Test seam: Kick normally queues a background pool pass, which can outlive a test's Dispose
    // and land in the NEXT test's static state. Fixtures disable the background and drive PassOnce
    // synchronously; AwaitQuiescenceForTests drains anything already in flight.
    private static int _backgroundDisabled;
    internal static bool BackgroundDisabledForTests
    {
        get => Volatile.Read(ref _backgroundDisabled) == 1;
        set => Volatile.Write(ref _backgroundDisabled, value ? 1 : 0);
    }

    /// <summary>Wait until no pass is running and none is queued; false on timeout. Tests call
    /// this in Dispose so no pool pass leaks into the next test's static state.</summary>
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

    /// <summary>Startup restore (including the legacy-pending migration). The in-memory marker stays null.</summary>
    internal static void LoadPersisted(string? target, bool landed, int gen) { lock (Gate) { _target = target; _landed = landed; _generation = gen; } }
    internal static void ResetForTests()
    {
        lock (Gate) { _target = null; _landed = false; _generation = 0; _lastObserved = null; _hasObserved = false; }
        Interlocked.Exchange(ref _applyBusy, 0);
        Interlocked.Exchange(ref _rerunRequested, 0);
    }

    // Target mutation: only ever inside a ConfigSaveLock persist (the single-writer invariant).
    internal static (string? t, bool landed, int gen) BackupTarget() => Snapshot();
    internal static void SetTargetUnderConfigSaveLock(string t) { lock (Gate) { _target = t; _landed = false; _generation++; } }
    internal static void RestoreTargetUnderConfigSaveLock((string? t, bool landed, int gen) b) { lock (Gate) { _target = b.t; _landed = b.landed; _generation = b.gen; } }
    /// <summary>/vlc/setup's "unchanged" path may clear the target only when it IS the requested value.</summary>
    internal static void ClearTargetIfMatchesUnderConfigSaveLock(string value) { lock (Gate) { if (_target == value) { _target = null; _landed = false; } } }

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
    internal static async Task AwaitNextPassCompletion(TimeSpan timeout)
    {
        var t = _passDone.Task;
        await Task.WhenAny(t, Task.Delay(timeout));
    }

    /// <summary>One pass on a worker: single-flight, broad catch (no secrets in the log), finally releases.</summary>
    internal static void PassOnce()
    {
        if (Interlocked.CompareExchange(ref _applyBusy, 1, 0) != 0) return;
        try { PassCore(); }
        catch (Exception ex) { Program.Log("VLC password-change pass failed (" + ex.GetType().Name + ")."); }
        finally
        {
            Interlocked.Exchange(ref _applyBusy, 0);
            SignalPassDone();
            if (Interlocked.Exchange(ref _rerunRequested, 0) == 1 && !BackgroundDisabledForTests) ThreadPool.QueueUserWorkItem(_ => PassOnce());
        }
    }

    private static void PassCore()
    {
        var (t, landed, gen) = SnapshotCommitted(); // never a staged-but-unsaved target
        if (t == null) return;
        var obs = VlcSetup.ReadHealthy();
        if (!obs.ReadOk) return; // a read glitch is not divergence: keep the marker, retry next pass
        // Divergence is recorded BEFORE any process gate, in BOTH branches: it is observation and
        // does not depend on write permission. Otherwise an old-VLC exit clobber followed by a new
        // VLC start would leave the marker at t and the later repair pass would see no edge - and a
        // kept phone that spent its grant on OLD would never re-arm.
        if (!obs.HealthyFor(t)) RecordDivergence(obs.Password);
        lock (LaunchWriteMutex)
        {
            VlcSetup.SweepTemp(); // under the mutex AND skipping fresh temps: never deletes Play's in-flight temp
            if (!landed) { ApplyTargetUnderMutex(t, landed, gen, obs); return; }
            if (obs.HealthyFor(t))
            {
                if (VlcSetup.VlcRunning()) return; // verify waits for quiet
                if (PersistFromPass(t, true, gen, setLanded: false, clearTarget: true, t))
                    Program.Log("VLC password change verified; the rotation is complete.");
            }
            else
            {
                if (VlcSetup.VlcRunning()) return; // repair waits for quiet
                // Exit-rewrite repair: an OLD-holding VLC rewrote vlcrc on exit; put the target back.
                if (VlcSetup.TryAtomicWriteHealthy(t) && VlcSetup.ReadHealthy().HealthyFor(t))
                {
                    if (PersistFromPass(t, true, gen, setLanded: false, clearTarget: false, t))
                        Program.Log("VLC password change re-applied after a VLC exit rewrote vlcrc.");
                }
            }
        }
    }

    /// <summary>The shared write step (the pass, Movies.Play's inline attempt, /vlc/setup's inline
    /// attempt): landed-by-observation first, defer on a process, else write the FULL healthy config
    /// + readback, then the SAME persist. The caller holds LaunchWriteMutex.</summary>
    internal static ApplyOutcome ApplyTargetUnderMutex(string t, bool landed, int gen, VlcSetup.VlcrcHealth obs)
    {
        if (!obs.ReadOk) return ApplyOutcome.WriteFailed; // never write over a vlcrc we could not read
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
        try
        {
            var (t, landed, gen) = SnapshotCommitted(); // never a staged-but-unsaved target
            if (t == null) return ApplyOutcome.NoTarget;
            var obs = VlcSetup.ReadHealthy();
            if (obs.ReadOk && !obs.HealthyFor(t)) RecordDivergence(obs.Password);
            lock (LaunchWriteMutex) { return ApplyTargetUnderMutex(t, landed, gen, obs); }
        }
        catch (Exception ex)
        {
            Program.Log("VLC password-change pass failed (" + ex.GetType().Name + ").");
            return ApplyOutcome.WriteFailed;
        }
        finally { Interlocked.Exchange(ref _applyBusy, 0); SignalPassDone(); }
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
                edge = _hasObserved && _lastObserved != t;
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
