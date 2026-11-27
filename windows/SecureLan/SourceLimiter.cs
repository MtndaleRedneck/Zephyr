using System.Net;
namespace SecureLan;

/// <summary>Per-source failure budget with backoff (spec section 2). IPv4 sources keyed by address. Fixed-size LRU. No global lockout.</summary>
public sealed class SourceLimiter
{
    sealed class Entry { public int Count; public DateTimeOffset WindowStart, LastFail, BlockedUntil; public int Level; public LinkedListNode<string>? Node; }
    readonly Func<DateTimeOffset> _clock;
    readonly int _capacity;
    readonly object _lock = new();
    readonly Dictionary<string, Entry> _map = new();
    readonly LinkedList<string> _lru = new();
    static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 16, 30 };

    public SourceLimiter(Func<DateTimeOffset> clock, int capacity = 1024) { _clock = clock; _capacity = capacity; }

    public static string Key(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return a.ToString();
    }

    void Touch(string key, Entry e)
    {
        if (e.Node != null) _lru.Remove(e.Node);
        e.Node = _lru.AddFirst(key);
    }

    /// <summary>True when the source is currently allowed to attempt.</summary>
    public bool IsAllowed(string key)
    {
        lock (_lock) return !_map.TryGetValue(key, out var e) || _clock() >= e.BlockedUntil;
    }

    public void RecordFailure(string key)
    {
        lock (_lock)
        {
            var now = _clock();
            if (!_map.TryGetValue(key, out var e))
            {
                if (_map.Count >= _capacity && _lru.Last != null) { _map.Remove(_lru.Last.Value); _lru.RemoveLast(); }
                e = new Entry { WindowStart = now };
                _map[key] = e;
            }
            if (now - e.LastFail > TimeSpan.FromMinutes(5)) { e.Count = 0; e.Level = 0; e.WindowStart = now; }
            if (now - e.WindowStart > TimeSpan.FromMinutes(1)) { e.Count = 0; e.WindowStart = now; }
            e.Count++; e.LastFail = now;
            if (e.Count >= 5)
            {
                e.BlockedUntil = now + TimeSpan.FromSeconds(BackoffSeconds[Math.Min(e.Level, BackoffSeconds.Length - 1)]);
                e.Level++;
            }
            Touch(key, e);
        }
    }

    /// <summary>A verified success (valid token or completed pairing) clears that source's failure state.</summary>
    public void RecordSuccess(string key)
    {
        lock (_lock)
        {
            if (_map.Remove(key, out var e) && e.Node != null) _lru.Remove(e.Node);
        }
    }

    public int Tracked { get { lock (_lock) return _map.Count; } }
}
