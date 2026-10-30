using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZephyrPlayer
{
    /// <summary>CP2 player-bar logic with no WPF in it, so it is unit-testable on its own:
    /// speed values, the sleep timer, the fullscreen pill clamp, bar reflow, the meta
    /// line and the libVLC subtitle options.</summary>
    public static class PlayerBarLogic
    {
        // ---------- speed (render 04) ----------

        /// <summary>The six chips, in grid order. The values are the 1.4 menu's
        /// (MainWindow.xaml.cs:448): 0.5, 0.75, 1, 1.25, 1.5, 2.</summary>
        public static readonly double[] SpeedChips = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
        public const double SpeedMin = 0.25, SpeedMax = 4.0, SpeedStep = 0.05;

        public static string ChipLabel(double rate) => Math.Abs(rate - 1.0) < 0.001 ? "Normal" : FormatRate(rate);

        /// <summary>"1.25×", "2×", "0.5×" (invariant digits, no trailing zeros).</summary>
        public static string FormatRate(double rate) =>
            Math.Round(rate, 2).ToString("0.##", CultureInfo.InvariantCulture) + "\u00D7";

        /// <summary>Fine slider value -> rate: clamped to 0.25..4 and snapped to 0.05.</summary>
        public static double SnapRate(double value)
        {
            var v = Math.Clamp(value, SpeedMin, SpeedMax);
            return Math.Round(Math.Round(v / SpeedStep) * SpeedStep, 2);
        }

        public static bool IsNormalRate(double rate) => Math.Abs(rate - 1.0) < 0.001;

        /// <summary>Index of the chip showing [rate], or -1 (a fine-slider value).</summary>
        public static int ChipIndex(double rate) => Array.FindIndex(SpeedChips, c => Math.Abs(c - rate) < 0.001);

        // ---------- fullscreen pill (render 03, Instinct U2) ----------

        /// <summary>The pill is the windowed bar laid out at up to 1000 px and drawn at 0.78:
        /// at most 780 px wide, never wider than the screen minus a 24 px gap each side.</summary>
        public const double PillScale = 0.78, PillLayoutMax = 1000, PillSideGap = 24, PillBottom = 28;
        public static double PillDrawnWidth(double viewport) => Math.Max(0, Math.Min(PillLayoutMax * PillScale, viewport - 2 * PillSideGap));
        public static double PillLayoutWidth(double viewport) => PillDrawnWidth(viewport) / PillScale;

        // ---------- bar reflow at narrow widths ----------

        /// <summary>Width of the volume group at each reflow level: 0 = speaker + 92 px
        /// track + "62%" (render 02), 1 = no % label, 2 = 56 px track, 3 = speaker only.</summary>
        public static readonly double[] VolumeGroupWidths = { 38 + 10 + 92 + 10 + 36, 38 + 10 + 92, 38 + 10 + 56, 38 };

        /// <summary>The smallest reflow that fits: each side column of the 3-column bar
        /// gets (inner - centre) / 2. Level 4 also drops the speed/sleep badges (the icons
        /// stay orange); 5 = nothing more to give.</summary>
        public static int ReflowLevel(double inner, double center, double right, double rightWithoutBadges)
        {
            var side = (inner - center) / 2;
            for (var level = 0; level < VolumeGroupWidths.Length; level++)
                if (VolumeGroupWidths[level] <= side + 0.5 && right <= side + 0.5) return level;
            if (VolumeGroupWidths[3] <= side + 0.5 && rightWithoutBadges <= side + 0.5) return 4;
            return 5;
        }

        // ---------- top overlay meta (render 02: "2012 · 1h 42m") ----------

        /// <summary>Year only when the file says it; duration as "1h 42m" / "42m" / "0m 6s".</summary>
        public static string MetaLine(string? year, long lengthMs)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(year))
            {
                var y = year.Trim();
                if (y.Length >= 4 && int.TryParse(y.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var yy) && yy >= 1880 && yy <= 2100) parts.Add(y.Substring(0, 4));
            }
            if (lengthMs > 0)
            {
                var s = lengthMs / 1000;
                parts.Add(s >= 3600 ? $"{s / 3600}h {s % 3600 / 60}m" : s >= 60 ? $"{s / 60}m" : $"{s}s");
            }
            return string.Join(" \u00B7 ", parts);
        }

        // ---------- subtitle delay (U5: 0.1 s steps) ----------

        public const int SubtitleDelayStepMs = 100;
        public static string FormatDelay(int ms) => (ms >= 0 ? "+" : "-") + (Math.Abs(ms) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

        // ---------- libVLC subtitle look (items 67, 66 size, carry-forward b) ----------

        /// <summary>libVLC 3's text size is 6.25 % of the picture height; render 02's
        /// line is about 26 px on a 667 px picture (3.9 %), so M = 62 %.</summary>
        public static int SubtitleTextScale(string size) => size switch { "S" => 50, "L" => 78, _ => 62 };

        public static string SubtitleSizeName(string size) => size == "S" ? "Small" : size == "L" ? "Large" : "Medium";

        /// <summary>Title of the restart popup shown when the subtitle size changes
        /// (Instinct CP2 round 4: the approved restart-to-take-effect fallback).</summary>
        public const string SubtitleRestartTitle = "Subtitle size saved";

        /// <summary>The restart popup's words. libVLC 3 reads the size only when Zephyr
        /// creates its LibVLC at app start (SubtitleSizePathTests), and closing the window
        /// keeps Zephyr running in the tray, so only Quit from the tray icon and opening
        /// Zephyr again applies it. Nothing about the playing movie changes.</summary>
        public static string SubtitleRestartNotice(string size) =>
            "Subtitle size is saved as " + SubtitleSizeName(size) + ".\n\n"
            + "The new size takes effect only after a full restart of Zephyr Player. "
            + "The subtitles on screen stay the size they are now.\n\n"
            + "To restart: right-click the Zephyr Player icon in the system tray (next to the clock), "
            + "choose Quit Zephyr Player, then open Zephyr Player again.\n\n"
            + "Closing the window is not enough, because Zephyr Player keeps running in the tray.\n\n"
            + "Your movie keeps playing. Zephyr will not restart or reopen it.";

        /// <summary>The LibVLC creation options for the subtitle look (size and style).
        /// libVLC 3 reads them when it is created; none of them can change on a playing
        /// movie. Position: none is set, so libVLC's default placement is used, the same
        /// place Windows VLC 3.0.24 draws them (Ezzy: "Should be the same location VLC PC
        /// uses"; CP2 round 5 removed the old 150 DIP --sub-margin lift). While the bar
        /// shows it can cover the line (disclosed, accepted); the bar auto-hides.
        /// [dpiScale] no longer changes any option (it scaled the removed lift).</summary>
        public static string[] SubtitleOptions(string size, double dpiScale)
        {
            return new[]
            {
                "--sub-text-scale=" + SubtitleTextScale(size).ToString(CultureInfo.InvariantCulture),
                "--freetype-font=Segoe UI",
                "--freetype-bold",
                "--freetype-color=16777215",
                "--freetype-outline-thickness=2",
                "--freetype-outline-opacity=255",
                "--freetype-shadow-opacity=200",
            };
        }
    }

    /// <summary>The sleep timer (render 04): Off, 15/30/45/60/90 minutes or the end of
    /// the movie. When it is up the movie PAUSES and the player stays (footnote "Pauses
    /// the movie when time is up").</summary>
    public sealed class SleepTimerState
    {
        public static readonly (string Label, int Minutes)[] Choices =
        {
            ("Off", 0), ("15 minutes", 15), ("30 minutes", 30), ("45 minutes", 45),
            ("1 hour", 60), ("1 hour 30", 90), ("End of this movie", EndOfMovie),
        };
        public const int EndOfMovie = -1;

        private DateTime _deadline;
        /// <summary>0 = off, minutes, or EndOfMovie.</summary>
        public int Minutes { get; private set; }
        public bool IsActive => Minutes != 0;
        public bool IsEndOfMovie => Minutes == EndOfMovie;

        public void Set(int minutes, DateTime now)
        {
            Minutes = minutes;
            _deadline = minutes > 0 ? now.AddMinutes(minutes) : DateTime.MaxValue;
        }
        public void Off() { Minutes = 0; _deadline = DateTime.MaxValue; }

        public TimeSpan Remaining(DateTime now) => Minutes > 0 ? (_deadline > now ? _deadline - now : TimeSpan.Zero) : TimeSpan.Zero;
        /// <summary>A timed choice whose time is up (the end-of-movie choice is never "up"
        /// by the clock; EndReached fires it).</summary>
        public bool IsDue(DateTime now) => Minutes > 0 && now >= _deadline;

        /// <summary>Bar badge: "27m", "45s" in the last minute, "End" for end of movie.</summary>
        public string Badge(DateTime now)
        {
            if (!IsActive) return "";
            if (IsEndOfMovie) return "End";
            var r = Remaining(now);
            return r.TotalSeconds >= 60 ? ((int)r.TotalMinutes) + "m" : ((int)Math.Ceiling(r.TotalSeconds)) + "s";
        }

        /// <summary>Pop-up header value: "27:41 left", "End of movie", or "" when off.</summary>
        public string Header(DateTime now)
        {
            if (!IsActive) return "";
            if (IsEndOfMovie) return "End of movie";
            var r = Remaining(now);
            var s = (long)Math.Ceiling(r.TotalSeconds);
            return (s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}") + " left";
        }
    }
}
