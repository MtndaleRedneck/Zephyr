using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ZephyrPlayer
{
    /// <summary>Local-file metadata only. Stream addresses and phone tokens never enter this file.</summary>
    internal sealed class PlaybackHistory
    {
        private static readonly object Sync = new();
        private readonly string _path;
        internal record Item(long PositionMs, long DurationMs, long FileSize, DateTime ModifiedUtc);
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
                    items[key] = new(Math.Clamp(positionMs, 0, durationMs), durationMs, f.Length, f.LastWriteTimeUtc);
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
