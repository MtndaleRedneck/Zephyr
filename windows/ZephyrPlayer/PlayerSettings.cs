// Player settings (separate identity from the BingeMote helper): JSON at
// %AppData%/ZephyrPlayer/settings.json. Tests pass their own file path.
// Fields: designated movies folder, volume
// boost max (slider always shows 0-100), adjustable skip lengths, bar color and
// icon styles, speed/sleep buttons hidden by default, remembered last volume.
using System;
using System.IO;
using System.Text.Json;

namespace ZephyrPlayer
{
    public sealed class PlayerSettings
    {
        private static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZephyrPlayer", "settings.json");
        private readonly string _path;
        private readonly object _lock = new object();

        public static readonly int[] BoostChoices = { 100, 125, 150, 175, 200 };
        public static readonly int[] PrimarySkipChoices = { 5, 10, 15, 30 };
        public static readonly int[] SecondarySkipChoices = { 15, 30, 60, 120, 180 };
        public static readonly string[] BarColorStyles = { "original", "orange", "white", "twotone", "inverse" };
        public static readonly string[] IconStyles = { "hollow", "filled" };

        /// <summary>Raised on the setting thread after any change is written.</summary>
        public event EventHandler? Changed;
        public event EventHandler? WriteFailed;
        private bool _warned;

        public PlayerSettings(string? path = null) { _path = path ?? DefaultPath; }

        public string LibraryFolder
        {
            get { lock (_lock) return Read().LibraryFolder ?? ""; }
            set { Write(d => d with { LibraryFolder = value }); }
        }

        /// <summary>Loudness boost ceiling in percent; the bar slider always reads 0-100.</summary>
        public int VolumeBoostMaxPct
        {
            get { lock (_lock) return Read().VolumeBoostMaxPct ?? 200; }
            set { if (Array.IndexOf(BoostChoices, value) < 0) return; Write(d => d with { VolumeBoostMaxPct = value }); }
        }

        // Legacy directional fields are read only as the initial pair intervals.
        // New values always control both directions of their own pair.
        public int PrimarySkipSeconds
        {
            get { lock (_lock) { var d = Read(); return ValidSkip(d.PrimarySkipSeconds ?? d.SkipBackSeconds, PrimarySkipChoices, 10); } }
            set { if (Array.IndexOf(PrimarySkipChoices, value) < 0) return; Write(d => d with { PrimarySkipSeconds = value }); }
        }

        public int SecondarySkipSeconds
        {
            get { lock (_lock) { var d = Read(); return ValidSkip(d.SecondarySkipSeconds ?? d.SkipForwardSeconds, SecondarySkipChoices, 30); } }
            set { if (Array.IndexOf(SecondarySkipChoices, value) < 0) return; Write(d => d with { SecondarySkipSeconds = value }); }
        }

        private static int ValidSkip(int? value, int[] choices, int fallback) =>
            value.HasValue && Array.IndexOf(choices, value.Value) >= 0 ? value.Value : fallback;

        /// <summary>Bar + Settings label: 30 -> "30s", 60 -> "1m" (the bar used to
        /// show raw seconds, reading "60"/"120"/"180" - teardown units bug).</summary>
        public static string FormatSkip(int seconds) => seconds >= 60 ? (seconds / 60) + "m" : seconds + "s";

        public int SkipDelta(bool secondary, bool backward) =>
            (secondary ? SecondarySkipSeconds : PrimarySkipSeconds) * (backward ? -1 : 1);

        public string BarColorStyle
        {
            // Unset default is "original" (render 06b: "Default is the original", the look of
            // render 02); a stored choice is never null, so an explicit pick always wins.
            get { lock (_lock) return Read().BarColorStyle ?? "original"; }
            set { if (Array.IndexOf(BarColorStyles, value) < 0) return; Write(d => d with { BarColorStyle = value }); }
        }

        public string IconStyle
        {
            get { lock (_lock) return Read().IconStyle ?? "hollow"; }
            set { if (Array.IndexOf(IconStyles, value) < 0) return; Write(d => d with { IconStyle = value }); }
        }

        /// <summary>"Flash on press": the brief colour flash of a pressed skip glyph. Render
        /// 06 shows it OFF; unset = off (U6), a stored value is kept on upgrade.</summary>
        public bool ButtonFlash
        {
            get { lock (_lock) return Read().ButtonFlash ?? false; }
            set { Write(d => d with { ButtonFlash = value }); }
        }

        /// <summary>"Animate on press" (item 80, a new key): primary rings turn and the
        /// secondary arrows get a light that runs to the tip. Unset = on (render 06).</summary>
        public bool AnimateOnPress
        {
            get { lock (_lock) return Read().AnimateOnPress ?? true; }
            set { Write(d => d with { AnimateOnPress = value }); }
        }

        public static readonly string[] SubtitleSizes = { "S", "M", "L" };
        /// <summary>Subtitle size S/M/L from the subtitles pop-up (render 04). libVLC 3 sets
        /// text size when its LibVLC instance is created (per-media options do not change it,
        /// SubtitleSizePathTests), so a change applies after Zephyr Player is quit and opened
        /// again (U4: never re-open a playing movie). Unset = M.</summary>
        public string SubtitleSize
        {
            get { lock (_lock) { var v = Read().SubtitleSize; return Array.IndexOf(SubtitleSizes, v) >= 0 ? v! : "M"; } }
            set { if (Array.IndexOf(SubtitleSizes, value) < 0) return; Write(d => d with { SubtitleSize = value }); }
        }

        public bool ShowSpeed
        {
            // On by default (render 06b toggles ON, item 57); a stored choice is kept (U8).
            get { lock (_lock) return Read().ShowSpeed ?? true; }
            set { Write(d => d with { ShowSpeed = value }); }
        }

        public bool ShowSleepTimer
        {
            get { lock (_lock) return Read().ShowSleepTimer ?? true; }
            set { Write(d => d with { ShowSleepTimer = value }); }
        }

        /// <summary>The player's own remembered volume, 0-100 (saved at exit, restored at open).</summary>
        public int LastVolume
        {
            get { lock (_lock) { var v = Read().LastVolume ?? 100; return Math.Clamp(v, 0, 100); } }
            set { Write(d => d with { LastVolume = Math.Clamp(value, 0, 100) }); }
        }

        public string LibrarySort
        {
            get { lock (_lock) return Read().LibrarySort == "created" ? "created" : "name"; }
            set { if (value is "name" or "created") Write(d => d with { LibrarySort = value }); }
        }

        public bool FoldersFirst
        {
            get { lock (_lock) return Read().FoldersFirst ?? true; }
            set { Write(d => d with { FoldersFirst = value }); }
        }

        /// <summary>The shell window's last restored bounds and maximized flag, as
        /// "left,top,width,height,max" in device-independent pixels; "" when never saved.
        /// Shared by every view (library and player): one window, one geometry.</summary>
        public string ShellBounds
        {
            get { lock (_lock) return Read().ShellBounds ?? ""; }
            set { Write(d => d with { ShellBounds = value }); }
        }

        private record Data(
            string? LibraryFolder,
            int? VolumeBoostMaxPct,
            int? SkipBackSeconds,
            int? SkipForwardSeconds,
            string? BarColorStyle,
            string? IconStyle,
            bool? ShowSpeed,
            bool? ShowSleepTimer,
            int? LastVolume,
            int? PrimarySkipSeconds = null,
            int? SecondarySkipSeconds = null,
            string? LibrarySort = null,
            bool? FoldersFirst = null,
            bool? ButtonFlash = null,
            string? ShellBounds = null,
            bool? AnimateOnPress = null,
            string? SubtitleSize = null);

        private bool _readFailed;
        private Data? _lastGood;

        private Data Read()
        {
            try
            {
                if (!File.Exists(_path)) { _readFailed = false; return _lastGood ?? Empty(); }
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? throw new InvalidDataException("Empty settings");
                _readFailed = false;
                return _lastGood = data;
            }
            catch { _readFailed = true; return _lastGood ?? Empty(); } // Keep original; refuse writes until readable.
        }

        private static Data Empty() => new(null, null, null, null, null, null, null, null, null);

        private void Write(Func<Data, Data> update)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var current = Read();
                if (_readFailed) { if (!_warned) { _warned = true; WriteFailed?.Invoke(this, EventArgs.Empty); } return; }
                _warned = false;
                var next = update(current);
                var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
                try {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(next));
                    if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
                    else File.Move(tmp, _path);
                    _lastGood = next;
                } finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
