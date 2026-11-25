// VLC's web remote, configured for him: the phone's Finish step hands the chosen password
// to the PC, and the PC writes extraintf=http + http-password into vlcrc, so a VLC opened
// by hand has the remote too. Written only while VLC is closed - a running VLC rewrites
// vlcrc on exit and would lose the change.
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
    /// Null in production, where the real process check runs.</summary>
    internal static Func<bool>? VlcRunningOverride;
    private static bool VlcRunning() => VlcRunningOverride?.Invoke() ?? Process.GetProcessesByName("vlc").Length > 0;

    private static bool _writeFaultLogged;

    /// <summary>Writes the web-remote setup into vlcrc. A file-system failure (locked file, no permission,
    /// unwritable profile) is not an exception for the caller: the password is kept as the pending write, which
    /// the retry timer completes when the disk allows, and the answer is the same "not on disk yet" shape the
    /// phone already understands (vlcPending). The log line is fixed text plus the exception type, once per fault.</summary>
    public static string WriteVlcrc(string password)
    {
        try { return WriteVlcrcCore(password); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (Program.ConfigSaveLock)
            {
                bool changed;
                lock (Gate) { changed = _pendingPassword != password; _pendingPassword = password; }
                if (changed) { try { Program.SaveConfig(); } catch (Exception) { Program.Log("Could not save the deferred vlcrc write."); } }
            }
            if (!_writeFaultLogged) { _writeFaultLogged = true; Program.Log("vlcrc could not be written (" + ex.GetType().Name + ") - the setup stays pending and will be retried."); }
            return JsonSerializer.Serialize(new { ok = false, reason = "vlcrc write failed" });
        }
    }

    private static string WriteVlcrcCore(string password)
    {
        // Round-5: the generated password is 96 bits; refuse to arm the LAN-facing web
        // remote with anything weak (12+ chars).
        if (password.Length < 12 || password.Length > 200 || password.Contains('\r') || password.Contains('\n'))
            return JsonSerializer.Serialize(new { ok = false, reason = "bad password" });
        var dir = VlcDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "vlcrc");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        // Unchanged settings need no write - and no deferral: a running VLC read exactly
        // this vlcrc at startup, so control already works now. Saying "pending" here would
        // falsely tell the user a reopen is required. Any older pending value is stale:
        // the newest request is already on disk. This check must come BEFORE the
        // VLC-running deferral, or it can never fire when VLC is up.
        if (FindValue(lines, "http-password") == password
            && (FindValue(lines, "extraintf") ?? "") == MergeValue(FindValue(lines, "extraintf"), "http")
            && FindValue(lines, "http-host") == "0.0.0.0")
        {
            bool hadPending;
            lock (Program.ConfigSaveLock)
            {
                lock (Gate) { hadPending = _pendingPassword != null; _pendingPassword = null; }
                if (hadPending) { try { Program.SaveConfig(); } catch (Exception ex) { Program.Log("Could not save: " + ex.Message); } }
            }
            _writeFaultLogged = false;
            Program.Log("vlcrc already has the web remote set up - nothing to write.");
            return JsonSerializer.Serialize(new { ok = true });
        }
        if (VlcRunning())
        {
            lock (Program.ConfigSaveLock)
            {
                lock (Gate) _pendingPassword = password;
                try { Program.SaveConfig(); } catch (Exception ex) { Program.Log("Could not save the deferred vlcrc write: " + ex.Message); } // the deferred write must survive a helper restart
            }
            Program.Log("vlcrc write skipped - VLC is running (it rewrites vlcrc on exit). Will retry when VLC closes.");
            return JsonSerializer.Serialize(new { ok = false, reason = "vlc running" });
        }
        // Merge, don't overwrite: another interface may already sit in extraintf.
        Upsert(lines, "extraintf", MergeValue(FindValue(lines, "extraintf"), "http"));
        Upsert(lines, "http-password", password);
        // Listen on every address (not just localhost) so a hand-opened VLC takes the phone
        // too; the password and the Private-only firewall rule guard it.
        Upsert(lines, "http-host", "0.0.0.0");
        File.WriteAllLines(path, lines);
        _writeFaultLogged = false;
        lock (Program.ConfigSaveLock)
        {
            lock (Gate) _pendingPassword = null;
            try { Program.SaveConfig(); } catch (Exception ex) { Program.Log("Could not save: " + ex.Message); } // clear any persisted deferred write
        }
        Program.Log("VLC web remote written into vlcrc.");
        return JsonSerializer.Serialize(new { ok = true });
    }

    private static readonly object Gate = new();
    private static string? _pendingPassword;

    /// <summary>VLC was open, so the write was skipped; retry when VLC closes.</summary>
    public static bool Pending => _pendingPassword != null;

    /// <summary>The deferred value itself (for config.json; vlcrc is plain text anyway).
    /// Null when nothing waits.</summary>
    public static string? PendingValue { get { lock (Gate) return _pendingPassword; } }

    /// <summary>The PC's attestation for the phone's setup check: vlcrc right now carries
    /// everything phone control needs - the web interface enabled (extraintf=http), a bind
    /// the phone can reach (http-host set and not loopback-only), and a live password - and
    /// no write is still waiting. A running VLC that has not reloaded yet reports Pending
    /// instead. Password equality alone is never enough.</summary>
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

    /// <summary>Re-arm a deferred write after a helper restart: VLC still running keeps it
    /// waiting for the retry timer; VLC already closed completes it immediately.</summary>
    public static void RestorePending(string password)
    {
        if (VlcRunning())
        {
            lock (Program.ConfigSaveLock)
            {
                lock (Gate) _pendingPassword = password;
                try { Program.SaveConfig(); } catch (Exception ex) { Program.Log("Could not save the restored deferred write: " + ex.Message); }
            }
            Program.Log("Restored the vlcrc write deferred before the restart - it completes when VLC closes.");
            return;
        }
        Program.Log("VLC closed while Zephyr VLC Remote was off - completing the deferred vlcrc write now.");
        WriteVlcrc(password);
    }

    /// <summary>Called on a timer: if an earlier write lost to a running VLC and VLC has
    /// since closed, write now. True while a write is still waiting.</summary>
    public static bool RetryPending()
    {
        string? pw;
        lock (Gate) pw = _pendingPassword;
        if (pw == null) return false;
        if (VlcRunning()) return true;
        Program.Log("VLC closed - writing the skipped vlcrc web-remote setup now.");
        using var doc = JsonDocument.Parse(WriteVlcrc(pw));
        return doc.RootElement.GetProperty("ok").GetBoolean() == false;
    }

    /// <summary>The password the phone must use right now: a deferred write wins over
    /// vlcrc (it becomes vlcrc the moment VLC closes), and vlcrc is re-read every call so
    /// a hand edit on the PC is picked up. null when nothing is configured yet.</summary>
    public static string? EffectivePassword()
    {
        lock (Gate) { if (_pendingPassword != null) return _pendingPassword; }
        return ReadVlcrcValue("http-password");
    }

    /// <summary>The automatic-first setup: the PC picks the password. Reuses the one
    /// already configured (including a write still waiting for VLC to close); generates a
    /// fresh one only when nothing exists. The value is never logged.</summary>
    public static string ReadOrCreatePassword() => ApplyPlannedPassword(PlanPassword());

    /// <summary>Decide the VLC password for a phone WITHOUT changing anything: the pending value, else the one
    /// in vlcrc (read once), else a fresh candidate (not stored, not written, not logged). Throws
    /// VlcrcUnreadableException when vlcrc exists but cannot be read, so an existing password is never replaced.
    /// The pair endpoint calls this BEFORE it saves the phone or spends the code.</summary>
    internal static string PlanPassword()
    {
        // ONE Gate hold covers the pending check, the single vlcrc read AND the candidate allocation. Another caller
        // (adopt) that plans, writes and clears between a read and an allocation would otherwise leave this caller
        // allocating a second password from a stale "nothing configured" read. Gate is a leaf lock here: the only
        // work inside it is a file read, and no code takes any other lock while holding Gate and then asks for it.
        lock (Gate)
        {
            string? existing = _pendingPassword;
            if (string.IsNullOrEmpty(existing)) existing = ReadVlcrcPasswordStrict();
            AfterPlanReadForTests?.Invoke();
            if (!string.IsNullOrEmpty(existing)) return existing;
            // Nothing configured yet. Every caller (pair, adopt) gets the SAME in-memory candidate until one of them
            // applies it. Memory only: not saved, not written, not pending.
            return _candidate ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        }
    }

    /// <summary>Test hook, null in production: runs inside PlanPassword after the vlcrc read, before allocation.</summary>
    internal static Action? AfterPlanReadForTests;

    private static string? _candidate;
    internal static void ForgetCandidateForTests() { lock (Gate) _candidate = null; }
    /// <summary>Test fixtures only (never called by the product): forget a deferred vlcrc password a previous test left behind.</summary>
    internal static void ForgetPendingForTests() { lock (Gate) _pendingPassword = null; }

    /// <summary>Carry out a plan: the idempotent write (also for an existing password, since a vlcrc can carry
    /// one with the web interface off). A write that cannot happen now stays pending (WriteVlcrc does not throw
    /// for file-system faults), so this returns the password either way.</summary>
    internal static string ApplyPlannedPassword(string password)
    {
        bool fresh = string.IsNullOrEmpty(ReadPendingOrNull()) && !File.Exists(Path.Combine(VlcDir, "vlcrc"));
        WriteVlcrc(password);
        lock (Gate) { if (_candidate == password) _candidate = null; }   // now on disk or pending: those are the truth
        if (fresh) Program.Log("VLC web-remote password generated for the phone (value never logged).");
        return password;
    }

    private static string? ReadPendingOrNull() { lock (Gate) return _pendingPassword; }

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
