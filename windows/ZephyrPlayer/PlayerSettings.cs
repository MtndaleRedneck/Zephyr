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
        public static readonly int[] SkipChoices = { 5, 10, 15, 30, 60 };
        public static readonly string[] BarColorStyles = { "original", "orange", "white", "twotone" };
        public static readonly string[] IconStyles = { "hollow", "filled" };

        /// <summary>Raised on the setting thread after any change is written.</summary>
        public event EventHandler? Changed;

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

        public int SkipBackSeconds
        {
            get { lock (_lock) return Read().SkipBackSeconds ?? 5; }
            set { if (Array.IndexOf(SkipChoices, value) < 0) return; Write(d => d with { SkipBackSeconds = value }); }
        }

        public int SkipForwardSeconds
        {
            get { lock (_lock) return Read().SkipForwardSeconds ?? 30; }
            set { if (Array.IndexOf(SkipChoices, value) < 0) return; Write(d => d with { SkipForwardSeconds = value }); }
        }

        public string BarColorStyle
        {
            get { lock (_lock) return Read().BarColorStyle ?? "original"; }
            set { if (Array.IndexOf(BarColorStyles, value) < 0) return; Write(d => d with { BarColorStyle = value }); }
        }

        public string IconStyle
        {
            get { lock (_lock) return Read().IconStyle ?? "hollow"; }
            set { if (Array.IndexOf(IconStyles, value) < 0) return; Write(d => d with { IconStyle = value }); }
        }

        public bool ShowSpeed
        {
            get { lock (_lock) return Read().ShowSpeed ?? false; }
            set { Write(d => d with { ShowSpeed = value }); }
        }

        public bool ShowSleepTimer
        {
            get { lock (_lock) return Read().ShowSleepTimer ?? false; }
            set { Write(d => d with { ShowSleepTimer = value }); }
        }

        /// <summary>The player's own remembered volume, 0-100 (saved at exit, restored at open).</summary>
        public int LastVolume
        {
            get { lock (_lock) { var v = Read().LastVolume ?? 100; return Math.Clamp(v, 0, 100); } }
            set { Write(d => d with { LastVolume = Math.Clamp(value, 0, 100) }); }
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
            int? LastVolume);

        private Data Read()
        {
            try
            {
                if (!File.Exists(_path)) return Empty();
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? Empty();
            }
            catch { return Empty(); } // unreadable settings = defaults, never fail-open
        }

        private static Data Empty() => new(null, null, null, null, null, null, null, null, null);

        private void Write(Func<Data, Data> update)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(update(Read())));
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
