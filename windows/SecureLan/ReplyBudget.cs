namespace SecureLan;

/// <summary>Discovery reply budget (spec section 4): per-source 2 replies per second, global 20 per second. One-second fixed windows, bounded table. Spoofed-source exhaustion is an accepted DoS tradeoff.</summary>
public sealed class ReplyBudget
{
    readonly Func<DateTimeOffset> _clock;
    readonly object _lock = new();
    readonly Dictionary<string, int> _perSource = new();
    readonly int _perSourceMax, _globalMax, _tableMax;
    long _windowSecond = long.MinValue;
    int _global;

    public ReplyBudget(Func<DateTimeOffset> clock, int perSourceMax = 2, int globalMax = 20, int tableMax = 256)
    { _clock = clock; _perSourceMax = perSourceMax; _globalMax = globalMax; _tableMax = tableMax; }

    /// <summary>True when one reply to this source may be sent now; consumes budget.</summary>
    public bool TryTake(string sourceKey)
    {
        lock (_lock)
        {
            long sec = _clock().ToUnixTimeSeconds();
            if (sec != _windowSecond) { _windowSecond = sec; _global = 0; _perSource.Clear(); }
            if (_global >= _globalMax) return false;
            _perSource.TryGetValue(sourceKey, out int n);
            if (n >= _perSourceMax) return false;
            if (n == 0 && _perSource.Count >= _tableMax) return false;   // bounded table: no new source this second
            _perSource[sourceKey] = n + 1; _global++;
            return true;
        }
    }
}
