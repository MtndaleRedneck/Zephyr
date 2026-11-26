// VLC's web remote, configured for him: the phone's Finish step hands the chosen password
// to the PC, and the PC writes extraintf=http + http-password into vlcrc, so a VLC opened
// by hand has the remote too. All vlcrc writes now go through the rotation pass
// (VlcRotation): this file owns vlcrc knowledge (reads, the healthy-config shape, the
// atomic write), the setup entry points, and the read-only credential accessors.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZephyrVlcRemote;

internal static class VlcSetup
{
    /// <summary>Test seam: the real vlcrc lives in the user's profile; tests redirect it
    /// so a suite run can never touch a real VLC configuration (round-7 N8).</summary>
    internal static string? VlcDirOverride;
    private static string VlcDir => VlcDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc");

    /// <summary>Test seam only: lets tests decide "is VLC running" instead of depending on the machine.
    /// Null in production, where the real process check runs. GetProcessesByName sees processes
    /// machine-wide (every session), which is the cross-session enumeration the rotation needs.</summary>
    internal static Func<bool>? VlcRunningOverride;
    internal static bool VlcRunning() => VlcRunningOverride?.Invoke() ?? Process.GetProcessesByName("vlc").Length > 0;

    /// <summary>The three-part healthy vlcrc: the password, the web interface enabled, and a
    /// bind the phone can reach. Password equality alone is never enough.</summary>
    internal sealed class VlcrcHealth
    {
        public string? Password;
        public bool InterfaceOn;
        public bool HostOk;
        /// <summary>False only when vlcrc EXISTS but could not be read (a transient lock, a denied
        /// path): callers must treat that as "unknown", never as divergence or as an empty file.</summary>
        public bool ReadOk;
        public bool HealthyFor(string t) => Password == t && InterfaceOn && HostOk;
    }

    /// <summary>Read vlcrc's health (the tag's own three conditions): password value, extraintf
    /// merged containing http (or its old name luahttp), and http-host == 0.0.0.0. An unreadable
    /// or missing file reports not-healthy; callers treat that as "write the full config".</summary>
    internal static VlcrcHealth ReadHealthy()
    {
        var h = new VlcrcHealth();
        try
        {
            var path = Path.Combine(VlcDir, "vlcrc");
            if (!File.Exists(path)) { h.ReadOk = true; return h; } // genuinely nothing there
            var lines = File.ReadAllLines(path).ToList();
            h.ReadOk = true;
            h.Password = FindValue(lines, "http-password");
            var extraintf = FindValue(lines, "extraintf") ?? "";
            var parts = extraintf.Split(new[] { ',', ':', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            h.InterfaceOn = parts.Contains("http") || parts.Contains("luahttp");
            h.HostOk = FindValue(lines, "http-host") == "0.0.0.0";
        }
        catch { /* exists but unreadable right now: ReadOk stays false, health unknown */ }
        return h;
    }

    /// <summary>The rotation pass's atomic write: temp + rename in the vlc dir, so no partial
    /// vlcrc is ever readable, and the FULL healthy config every time - merge-upsert extraintf
    /// (another interface already there is never dropped), the password, and the 0.0.0.0 bind.
    /// Never a password-only line.</summary>
    internal static bool TryAtomicWriteHealthy(string password)
    {
        var dir = VlcDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "vlcrc");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        Upsert(lines, "extraintf", MergeValue(FindValue(lines, "extraintf"), "http"));
        Upsert(lines, "http-password", password);
        // Listen on every address (not just localhost) so a hand-opened VLC takes the phone
        // too; the password and the Private-only firewall rule guard it.
        Upsert(lines, "http-host", "0.0.0.0");
        var tmp = Path.Combine(dir, "vlcrc.zephyr-tmp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllLines(tmp, lines);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return true;
    }

    /// <summary>Leftover temp files from interrupted writes. Runs under the launch/write mutex
    /// AND skips fresh files, so a concurrent writer's in-flight temp is never deleted.</summary>
    internal static void SweepTemp()
    {
        try
        {
            var dir = VlcDir;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "vlcrc.zephyr-tmp-*"))
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromSeconds(10)) File.Delete(f);
                }
                catch { }
        }
        catch { }
    }

    private static readonly object Gate = new();
    private static string? _candidate;

    /// <summary>Pending is the rotation's published state: a write genuinely outstanding
    /// (target set, not yet landed). Phones already handle exactly this shape from setup deferrals.</summary>
    public static bool Pending => VlcRotation.PendingPublished;

    /// <summary>The PC's attestation for the phone's setup check: vlcrc right now carries
    /// everything phone control needs - the web interface enabled (extraintf=http), a bind
    /// the phone can reach (http-host set and not loopback-only), and a live password - and
    /// no write is still waiting. Password equality alone is never enough.</summary>
    public static bool Configured
    {
        get
        {
            if (Pending) return false;
            if (string.IsNullOrEmpty(ReadVlcrcValue("http-password"))) return false;
            var extraintf = ReadVlcrcValue("extraintf") ?? "";
            var parts = extraintf.Split(new[] { ',', ':', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            if (!parts.Contains("http") && !parts.Contains("luahttp")) return false; // interface off
            var host = ReadVlcrcValue("http-host");
            if (string.IsNullOrEmpty(host)) return false; // VLC's default binds localhost only
            if (host is "127.0.0.1" or "localhost" or "::1") return false; // the phone could never reach it
            return true;
        }
    }

    /// <summary>APPLIED: vlcrc's password, re-read every call so a hand edit on the PC is picked
    /// up. Sole exception: vlcrc has NO password value at all (fresh install before any setup) ->
    /// the rotation target, which is served and used exactly like today's pending value.</summary>
    public static string? EffectivePassword()
    {
        var pw = ReadVlcrcValue("http-password");
        if (!string.IsNullOrEmpty(pw)) return pw;
        return VlcRotation.TargetValue;
    }

    /// <summary>The 96-bit generator every password in the product shares. Never logged.</summary>
    internal static string GeneratePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>The phone's Finish step: validate, then drive the rotation target. When vlcrc
    /// already carries exactly this healthy config there is nothing to do (and only a target
    /// for THIS value may clear). Otherwise the target persists first (survives a restart), the
    /// pass is kicked, and one synchronous inline attempt decides the truthful reply: ok only
    /// after an actual landing, "vlc running" while a process blocks the write.</summary>
    public static string SetupFromPhone(string password)
    {
        // The generated password is 96 bits; refuse to arm the LAN-facing web remote with anything weak.
        if (password.Length < 12 || password.Length > 200 || password.Contains('\r') || password.Contains('\n'))
            return JsonSerializer.Serialize(new { ok = false, reason = "bad password" });
        if (ReadHealthy().HealthyFor(password))
        {
            var backup = VlcRotation.BackupTarget();
            Program.MutateAndPersist(() => VlcRotation.ClearTargetIfMatchesUnderConfigSaveLock(password),
                () => VlcRotation.RestoreTargetUnderConfigSaveLock(backup), "the VLC setup");
            Program.Log("vlcrc already has the web remote set up - nothing to write.");
            return JsonSerializer.Serialize(new { ok = true });
        }
        // Stale-value guard: never stage over a NEWER truth. A live rotation target of a
        // different value belongs to a newer writer (a removal rotation); a vlcrc carrying a
        // different password is the applied credential. Either means THIS phone's value is
        // stale (a late Finish with the old password): refuse, so the phone re-fetches the
        // current one through adopt instead of reverting the PC to the old password. An
        // UNREADABLE vlcrc is unknown, not empty: refuse a fresh stage then as well. The outer
        // checks are the fast path; the SAME checks rerun INSIDE the transaction, because a
        // removal can stage (and a pass can land) between them and the persist.
        var liveTarget = VlcRotation.TargetValue;
        string? applied;
        try { applied = ReadVlcrcPasswordStrict(); }
        catch (VlcrcUnreadableException)
        {
            if (liveTarget == password)
                applied = null; // re-setting the existing target's value: the write attempt below decides
            else
            {
                Program.Log("vlcrc could not be read during a phone setup - left the rotation state untouched.");
                return JsonSerializer.Serialize(new { ok = false, reason = "vlcrc unreadable" });
            }
        }
        if ((liveTarget != null && liveTarget != password) || (!string.IsNullOrEmpty(applied) && applied != password))
        {
            Program.Log("A phone sent a stale VLC password to /vlc/setup - refused; it must fetch the current one (adopt).");
            return JsonSerializer.Serialize(new { ok = false, reason = "password changed" });
        }
        var refuseReason = (string?)null;
        var staged = false;
        (string? t2, bool l2, int g2) backup = default;
        var okPersist = Program.MutateAndPersist(() =>
            {
                // Authoritative recheck under ConfigSaveLock: every rotation's target persists
                // BEFORE its vlcrc write can land, and vlcrc itself is re-read here, so no
                // interleaved rotation can be overwritten by this stage. Same fail-closed rule
                // as the outer guard: an unreadable vlcrc is unknown, not empty - only a
                // same-value re-set of the existing target may proceed to the write attempt.
                var targetNow = VlcRotation.TargetValue;
                if (targetNow != null && targetNow != password) { refuseReason = "password changed"; return; }
                string? appliedNow;
                try { appliedNow = ReadVlcrcPasswordStrict(); }
                catch (VlcrcUnreadableException)
                {
                    if (targetNow == password) appliedNow = null; // the write attempt below decides
                    else { refuseReason = "vlcrc unreadable"; return; }
                }
                if (!string.IsNullOrEmpty(appliedNow) && appliedNow != password) { refuseReason = "password changed"; return; }
                backup = VlcRotation.BackupTarget(); // inside the transaction: the rollback restores THIS state
                VlcRotation.SetTargetUnderConfigSaveLock(password);
                staged = true;
            },
            () => { if (staged) VlcRotation.RestoreTargetUnderConfigSaveLock(backup); }, "the VLC setup");
        if (refuseReason != null)
        {
            Program.Log("A phone's VLC setup was refused (" + refuseReason + ") - left the rotation state untouched.");
            return JsonSerializer.Serialize(new { ok = false, reason = refuseReason });
        }
        if (!okPersist)
            return JsonSerializer.Serialize(new { ok = false, reason = "vlcrc write failed" });
        VlcRotation.Kick();
        var outcome = VlcRotation.TryInlinePass();
        if (outcome == VlcRotation.ApplyOutcome.Landed || ReadHealthy().HealthyFor(password))
        {
            Program.Log("VLC web remote written into vlcrc.");
            return JsonSerializer.Serialize(new { ok = true });
        }
        if (outcome == VlcRotation.ApplyOutcome.Deferred)
        {
            // A running VLC holds the write (or another pass is mid-flight and converging).
            Program.Log("vlcrc write skipped - VLC is running (it rewrites vlcrc on exit). Will retry when VLC closes.");
            return JsonSerializer.Serialize(new { ok = false, reason = "vlc running" });
        }
        Program.Log("vlcrc could not be written - the change stays pending and will be retried.");
        return JsonSerializer.Serialize(new { ok = false, reason = "vlcrc write failed" });
    }

    /// <summary>Decide the VLC password for a phone WITHOUT changing anything: vlcrc FIRST (the
    /// APPLIED credential is the truth), else - the setup exception only - the rotation target,
    /// else one shared in-memory candidate (not stored, not written, not logged). Throws
    /// VlcrcUnreadableException when vlcrc exists but cannot be read, so an existing password is
    /// never replaced. The pair endpoint calls this BEFORE it saves the phone or spends the code.</summary>
    internal static string PlanPassword()
    {
        // ONE Gate hold covers the single vlcrc read AND the candidate allocation, so two
        // callers can never allocate different passwords from the same "nothing configured" read.
        lock (Gate)
        {
            string? existing = ReadVlcrcPasswordStrict();
            AfterPlanReadForTests?.Invoke();
            if (!string.IsNullOrEmpty(existing)) return existing;
            var t = VlcRotation.TargetValue;
            if (!string.IsNullOrEmpty(t)) return t;
            return _candidate ??= GeneratePassword();
        }
    }

    /// <summary>Test hook, null in production: runs inside PlanPassword after the vlcrc read, before allocation.</summary>
    internal static Action? AfterPlanReadForTests;

    internal static void ForgetCandidateForTests() { lock (Gate) _candidate = null; }

    /// <summary>Carry out a plan. STRICT read: a vlcrc that cannot be read is unknown, not "no
    /// password" - and a read failure is read-only, never a target overwrite. With vlcrc carrying
    /// a password this is READ-ONLY: pair and adopt never touch the rotation target - a value
    /// planned from an OLD vlcrc must never replace an outstanding NEW target mid-deferral. Only
    /// the genuine setup exception - no vlcrc password AND no rotation target at all - stages the
    /// planned value as the target (persisted, then a kick). A live target is never overwritten
    /// here even when it differs from the plan: it belongs to a newer writer (a removal rotation),
    /// and the phone heals onto it by adoption after the landing.</summary>
    internal static string ApplyPlannedPassword(string password)
    {
        string? existing;
        try { existing = ReadVlcrcPasswordStrict(); }
        catch (VlcrcUnreadableException)
        {
            lock (Gate) { if (_candidate == password) _candidate = null; }
            Program.Log("vlcrc could not be read while applying a plan - left the rotation state untouched.");
            return password;
        }
        if (!string.IsNullOrEmpty(existing))
        {
            lock (Gate) { if (_candidate == password) _candidate = null; }
            return password;
        }
        var fresh = !File.Exists(Path.Combine(VlcDir, "vlcrc"));
        if (VlcRotation.TargetValue == null)
        {
            // Recheck INSIDE the mutation (serialized under ConfigSaveLock): a removal that
            // staged its own NEW target between the check above and this transaction must win -
            // the setup exception never overwrites a live target, even one staged mid-call.
            (string? t, bool landed, int gen) backup = default;
            var staged = false;
            Program.MutateAndPersist(() =>
                {
                    if (VlcRotation.TargetValue == null)
                    {
                        backup = VlcRotation.BackupTarget();
                        VlcRotation.SetTargetUnderConfigSaveLock(password);
                        staged = true;
                    }
                },
                () => { if (staged) VlcRotation.RestoreTargetUnderConfigSaveLock(backup); },
                "the VLC password setup");
            if (staged)
            {
                VlcRotation.Kick();
                if (fresh) Program.Log("VLC web-remote password generated for the phone (value never logged).");
            }
        }
        lock (Gate) { if (_candidate == password) _candidate = null; }   // the target is the truth now
        return password;
    }

    /// <summary>Adopt serving: READ-ONLY whenever vlcrc carries a password (never a write, never
    /// a candidate, never a target mutation). Only the setup exception (vlcrc has NO password)
    /// plans the shared candidate and stages it as the rotation target; the caller's own
    /// ConfigSaveLock persist saves it (one save with the grant markers) and kicks after the lock.</summary>
    internal static string ServeForAdoptInMemory()
    {
        var existing = ReadVlcrcPasswordStrict(); // throws VlcrcUnreadableException on an unreadable vlcrc
        if (!string.IsNullOrEmpty(existing)) return existing;
        lock (Gate)
        {
            var t = VlcRotation.TargetValue;
            if (string.IsNullOrEmpty(t)) t = _candidate ??= GeneratePassword();
            if (VlcRotation.TargetValue != t) VlcRotation.SetTargetUnderConfigSaveLock(t);
            return t;
        }
    }

    /// <summary>The configured http-password, read ONCE. Missing file or no password line is null (nothing
    /// configured, a new one may be generated). Anything else that stops the read (a lock, a denied or untraversable path, a
    /// directory where the file should be) is NOT "nothing configured": it throws, so an existing password is never replaced on a transient lock.</summary>
    private static string? ReadVlcrcPasswordStrict()
    {
        var path = Path.Combine(VlcDir, "vlcrc");
        // No File.Exists pre-check: it returns false for a file it merely cannot reach (untraversable parent, access
        // fault), which would read as "nothing configured". Only a file or folder that is really not there is null.
        try { return FindValue(File.ReadAllLines(path).ToList(), "http-password"); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new VlcrcUnreadableException(); }
    }

    /// <summary>The live (uncommented) value of a vlcrc key, or null. Used for the
    /// web-remote password the resume poller needs.</summary>
    internal static string? ReadVlcrcValue(string key)
    {
        try
        {
            var path = Path.Combine(VlcDir, "vlcrc");
            if (!File.Exists(path)) return null;
            return FindValue(File.ReadAllLines(path).ToList(), key);
        }
        catch { return null; }
    }

    private static string? FindValue(List<string> lines, string key)
    {
        var prefix = key + "=";
        foreach (var line in lines)
        {
            var raw = line.TrimStart();
            if (raw.StartsWith('#')) continue; // only a live setting counts
            if (raw.StartsWith(prefix, StringComparison.Ordinal)) return raw[prefix.Length..].Trim();
        }
        return null;
    }

    private static string MergeValue(string? existing, string wanted)
    {
        if (string.IsNullOrEmpty(existing)) return wanted;
        // VLC's own preferences writer separates with ':' ("rc:http") and its loader also
        // takes space and ','; "luahttp" is its old name for the same interface.
        var parts = existing.Split(new[] { ',', ':', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim()).ToList();
        if (parts.Contains(wanted) || (wanted == "http" && parts.Contains("luahttp"))) return existing;
        parts.Add(wanted);
        return string.Join(",", parts);
    }

    /// <summary>Replace the (usually commented-out) vlcrc line for a key, or append it.</summary>
    private static void Upsert(List<string> lines, string key, string value)
    {
        var prefix = key + "=";
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].TrimStart().TrimStart('#').TrimStart();
            if (t.StartsWith(prefix, StringComparison.Ordinal))
            {
                lines[i] = prefix + value;
                return;
            }
        }
        lines.Add(prefix + value);
    }
}

/// <summary>vlcrc exists but could not be read, so whether a web-remote password is already set is unknown.</summary>
internal sealed class VlcrcUnreadableException : IOException
{
    public VlcrcUnreadableException() : base("vlcrc exists but could not be read") { }
}
