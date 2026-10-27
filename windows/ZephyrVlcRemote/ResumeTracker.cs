// Remembers where each movie was left, so the phone can offer Resume / Start over the
// next time it is tapped. The helper polls VLC's own web interface every 5 s while VLC
// runs (it already carries the password), keeps the place per file on disk, and suppresses
// VLC's own "continue?" prompt for phone-launched plays.
using System.Diagnostics;
using System.Text.Json;

namespace ZephyrVlcRemote;

internal static class ResumeTracker
{
    private sealed class Entry
    {
        public double Position { get; set; }
        public double Length { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> ByFile = new(StringComparer.OrdinalIgnoreCase);
    private static string? _password;
    private static int _port = 8080;
    private static bool _pollerStarted;
    private static DateTime _lastSave = DateTime.MinValue;
    private static string? _currentFile;
    private static bool _dirty;
    private static bool _saveSuppressed;
    // Keys cleared during a read-only window: a later successful disk read must NOT
    // resurrect them (round-8 N-6).
    private static readonly HashSet<string> ClearedWhileSuppressed = new(StringComparer.OrdinalIgnoreCase);
    private static int _generation; // stale retry loops (or tests resetting state) stop here

    // Settled windows: under a minute watched is not worth asking about; the last couple
    // of minutes counts as finished.
    internal const long MinAskSec = 60;
    internal const long TailSec = 120;

    // Test-only seam: points the store at a temp folder.
    internal static string? StoreDirOverride;

    private static string StorePath =>
        Path.Combine(StoreDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zephyr VLC Remote"), "resume-places.json");

    /// <summary>The saved place worth asking about, or null (barely started / nearly done / never seen).</summary>
    public static (long position, long length)? Ask(string file)
    {
        lock (Gate)
        {
            if (!ByFile.TryGetValue(file, out var e)) return null;
            if (e.Position < MinAskSec || e.Length - e.Position <= TailSec) return null;
            return ((long)e.Position, (long)e.Length);
        }
    }

    public static void Clear(string file)
    {
        lock (Gate)
        {
            if (ByFile.Remove(file)) _dirty = true;
            // Tombstone even when memory held nothing: the unreadable DISK copy may
            // still hold this file, and a retry merge must not bring it back.
            if (_saveSuppressed) { ClearedWhileSuppressed.Add(file); _dirty = true; }
            SaveLocked();
        }
    }

    /// <summary>A phone-launched play hands the web-remote password over; remember it and
    /// make sure the poller is running.</summary>
    public static void NoteLaunch(int port, string password)
    {
        lock (Gate) { _port = port; if (password.Length > 0) _password = password; }
        EnsurePoller();
    }

    /// <summary>Called once at helper startup: load saved places; poll if the password is known.</summary>
    public static void Start()
    {
        try
        {
            var path = StorePath;
            if (File.Exists(path))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (map != null) lock (Gate) { foreach (var kv in map) ByFile[kv.Key] = kv.Value; }
            }
        }
        catch (Exception ex)
        {
            // An unreadable places file is NEVER moved, renamed or overwritten by the
            // app: it is the only copy of the saved places, and no reset step happens
            // without the owner's explicit OK. Stay read-only for this session and
            // retry shortly; reopening the helper tries again too.
            lock (Gate) _saveSuppressed = true;
            Program.Log("Saved playback places could not be read (" + ex.GetType().Name
                + "); the file is left untouched and no resume data will be written over it. Trying again shortly.");
            var generation = _generation;
            Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 15 : 60));
                    lock (Gate) { if (_generation != generation) return; } // state was reset
                    if (TryReadOnce())
                    {
                        Program.Log("Saved playback places read successfully on retry.");
                        return;
                    }
                }
                Program.Log("Saved playback places are still unreadable; staying read-only until the helper is reopened.");
            });
        }
        var pw = VlcSetup.EffectivePassword();
        if (pw != null && pw.Length > 0)
        {
            lock (Gate) _password ??= pw;
            EnsurePoller();
        }
    }

    /// <summary>One disk read + merge; true when the file read and parsed.</summary>
    private static bool TryReadOnce()
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(StorePath));
            if (map == null) return false;
            lock (Gate) { MergeDiskLocked(map); }
            return true;
        }
        catch { return false; }
    }

    // ----- test seams (InternalsVisibleTo) -----
    internal static bool SaveSuppressedForTests { get { lock (Gate) return _saveSuppressed; } }
    internal static bool RetryNowForTests() => TryReadOnce();
    internal static void RecordForTests(string file, double position, double length)
    {
        lock (Gate) { ByFile[file] = new Entry { Position = position, Length = length }; _dirty = true; }
    }
    internal static void ResetForTests(string storeDir)
    {
        lock (Gate)
        {
            ByFile.Clear(); ClearedWhileSuppressed.Clear();
            _saveSuppressed = false; _dirty = false; _currentFile = null;
            _generation++;
            StoreDirOverride = storeDir;
        }
    }

    /// <summary>Merge a successfully-read disk copy into memory (round-8 N-6): disk only
    /// FILLS GAPS. A position recorded in memory during the read-only window is newer
    /// and stays; a key cleared in that window is tombstoned and must not come back.
    /// Anything memory-only or tombstoned is then persisted, lifting read-only.</summary>
    private static void MergeDiskLocked(Dictionary<string, Entry> map)
    {
        foreach (var kv in map)
            if (!ByFile.ContainsKey(kv.Key) && !ClearedWhileSuppressed.Contains(kv.Key))
                ByFile[kv.Key] = kv.Value;
        if (ClearedWhileSuppressed.Count > 0) _dirty = true;
        ClearedWhileSuppressed.Clear();
        _saveSuppressed = false;
        if (_dirty) SaveLocked();
    }

    private static void EnsurePoller()
    {
        lock (Gate)
        {
            if (_pollerStarted) return;
            _pollerStarted = true;
        }
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(5000);
                try { await PollOnce(); } catch { }
            }
        });
    }

    private static async Task PollOnce()
    {
        string? pw; int port;
        lock (Gate) { pw = _password; port = _port; }
        if (pw == null) return;
        if (Process.GetProcessesByName("vlc").Length == 0)
        {
            // VLC closed: forget the "current" file (its last place is already saved).
            // Save only when something actually changed - not every 5 s while VLC is
            // closed, which would mean thousands of identical writes a day.
            lock (Gate) { _currentFile = null; if (_dirty) SaveLocked(); }
            return;
        }
        var status = await Get(port, pw, "status.json");
        if (status == null) return;
        using var doc = JsonDocument.Parse(status);
        var root = doc.RootElement;
        if (!root.TryGetProperty("time", out var t) || !root.TryGetProperty("length", out var l)) return;
        var state = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
        if (state != "playing" && state != "paused") return;
        var file = await CurrentFile(port, pw);
        if (file == null) return;
        lock (Gate)
        {
            _currentFile = file;
            ByFile[file] = new Entry { Position = t.GetDouble(), Length = l.GetDouble() };
            _dirty = true;
            if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(30)) SaveLocked();
        }
    }

    /// <summary>The file VLC is playing, from the playlist's current item URI.</summary>
    private static async Task<string?> CurrentFile(int port, string pw)
    {
        var pl = await Get(port, pw, "playlist.json");
        if (pl == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(pl);
            foreach (var node in doc.RootElement.GetProperty("children")[0].GetProperty("children").EnumerateArray())
            {
                if (node.TryGetProperty("current", out _)
                        && node.TryGetProperty("uri", out var uri))
                {
                    var u = new Uri(uri.GetString()!);
                    return u.IsFile ? u.LocalPath : null;
                }
            }
        }
        catch { }
        return null;
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static async Task<string?> Get(int port, string pw, string page)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/requests/{page}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + pw)));
            using var resp = await _http.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
    }

    private static readonly object SaveGate = new();

    private static void SaveLocked()
    {
        if (_saveSuppressed || !_dirty) return;
        try
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(ByFile);
            // Atomic (round-5): a crash mid-save must never tear the places file.
            lock (SaveGate)
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            _lastSave = DateTime.UtcNow;
            _dirty = false;
        }
        catch { }
    }
}
