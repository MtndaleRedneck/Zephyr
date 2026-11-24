using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZephyrPlayer
{
    /// <summary>Local-file metadata only. Stream addresses and phone tokens never enter this file.</summary>
    internal sealed class PlaybackHistory
    {
        private static readonly object Sync = new();
        private readonly string _path;
        internal record Item(long PositionMs, long DurationMs, long FileSize, DateTime ModifiedUtc, DateTime PlayedUtc = default, string? Path = null);
        public PlaybackHistory(string? path = null) => _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZephyrPlayer", "playback-history.json");
        internal static string? LocalPath(string source)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri)) return uri.IsFile ? uri.LocalPath : null;
            return Path.IsPathFullyQualified(source) ? source : null;
        }
        private Dictionary<string, Item> Read()
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, Item>>(File.ReadAllText(_path)) ?? new(); }
            catch { return new(); }
        }
        public Item? Get(string source)
        {
            var path = LocalPath(source);
            if (path == null) return null;
            lock (Sync)
            {
                var items = Read();
                if (!items.TryGetValue(path.ToUpperInvariant(), out var value)) return null;
                try { var f = new FileInfo(path); return f.Length == value.FileSize && f.LastWriteTimeUtc == value.ModifiedUtc ? value : null; }
                catch { return null; }
            }
        }
        /// <summary>Movies started and not finished, newest first: started more than 10 s in, under 95% done,
        /// file still the same size and date, and still under <paramref name="root"/> when given.</summary>
        internal List<(string Path, Item Item)> Recent(int count, string? root = null)
        {
            var result = new List<(string, Item)>();
            if (count <= 0) return result;
            lock (Sync)
            {
                var rootFull = string.IsNullOrEmpty(root) ? null : System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                foreach (var pair in Read().OrderByDescending(p => p.Value.PlayedUtc))
                {
                    var v = pair.Value;
                    if (v.PlayedUtc == default || string.IsNullOrEmpty(v.Path) || v.PositionMs < 10_000 || v.DurationMs <= 0 || v.PositionMs >= v.DurationMs * 0.95) continue;
                    try
                    {
                        // Keys are stored upper-case; the file system tells us the real name.
                        var info = new FileInfo(v.Path!);
                        if (!info.Exists || info.Length != v.FileSize || info.LastWriteTimeUtc != v.ModifiedUtc) continue;
                        var real = v.Path!;
                        if (rootFull != null && !real.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                        result.Add((real, v));
                        if (result.Count >= count) break;
                    }
                    catch { }
                }
            }
            return result;
        }
        internal static string TimeLeftLabel(long positionMs, long durationMs)
        {
            var left = Math.Max(0, durationMs - positionMs);
            var minutes = (int)Math.Ceiling(left / 60000.0);
            return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min left" : $"{Math.Max(1, minutes)} min left";
        }
        public void Save(string source, long positionMs, long durationMs, bool metadataOnly = false)
        {
            var path = LocalPath(source);
            if (path == null || durationMs <= 0) return;
            lock (Sync)
            {
                try
                {
                    var f = new FileInfo(path);
                    var items = Read();
                    var key = path.ToUpperInvariant();
                    if (metadataOnly && items.TryGetValue(key, out var old) && old.FileSize == f.Length && old.ModifiedUtc == f.LastWriteTimeUtc) return;
                    var played = metadataOnly ? (items.TryGetValue(key, out var prior) ? prior.PlayedUtc : default) : DateTime.UtcNow;
                    items[key] = new(Math.Clamp(positionMs, 0, durationMs), durationMs, f.Length, f.LastWriteTimeUtc, played, metadataOnly && items.TryGetValue(key, out var prior2) ? prior2.Path : path);
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var temp = _path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(items));
                    File.Move(temp, _path, true);
                }
                catch { } // An unreadable/removable library must not interrupt playback.
            }
        }
        internal static string DurationLabel(long ms) => ms <= 0 ? "" : ms >= 3600000
            ? $"{ms / 3600000}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}"
            : $"{ms / 60000}:{ms / 1000 % 60:00}";
    }
}
