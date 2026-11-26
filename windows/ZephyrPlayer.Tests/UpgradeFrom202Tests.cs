using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Data written by Zephyr Player 2.0.2 (same per-user folder, same file names) must load in 3.0.0.
    /// The fixtures below are literal 2.0.2-shaped files, including keys 3.0.0 no longer has.
    /// This covers settings.json and playback-history.json. paired.json, its DPAPI wrapper and the library code are byte-identical to the player-v2.0.2 tag (git diff empty), so they have no test here. The installer-over-installer path is a separate CI step.
    /// </summary>
    public class UpgradeFrom202Tests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "zp-upg-" + Guid.NewGuid().ToString("N"));
        public UpgradeFrom202Tests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private const string Settings202 = "{\"LibraryFolder\":\"D:\\\\Movies\",\"VolumeBoostMaxPct\":150,\"SkipBackSeconds\":5,\"SkipForwardSeconds\":60," +
            "\"BarColorStyle\":\"white\",\"IconStyle\":\"filled\",\"ShowSpeed\":true,\"ShowSleepTimer\":false,\"LastVolume\":42," +
            "\"PrimarySkipSeconds\":15,\"SecondarySkipSeconds\":120,\"LibrarySort\":\"created\",\"FoldersFirst\":false,\"ButtonFlash\":true," +
            "\"ShellBounds\":\"10,20,1280,720,0\",\"AnimateOnPress\":false,\"SubtitleSize\":\"L\",\"RememberVolume\":false}";

        [Fact]
        public void Settings202_KeepsEveryStoredChoice()
        {
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path, Settings202);
            var s = new PlayerSettings(path);
            bool warned = false; s.WriteFailed += (_, _) => warned = true;
            Assert.Equal("D:\\Movies", s.LibraryFolder);
            Assert.Equal(150, s.VolumeBoostMaxPct);
            Assert.Equal(15, s.PrimarySkipSeconds);
            Assert.Equal(120, s.SecondarySkipSeconds);
            Assert.Equal("white", s.BarColorStyle);
            Assert.Equal("filled", s.IconStyle);
            Assert.True(s.ShowSpeed);
            Assert.False(s.ShowSleepTimer);
            Assert.Equal(42, s.LastVolume);
            Assert.Equal("created", s.LibrarySort);
            Assert.False(s.FoldersFirst);
            Assert.True(s.ButtonFlash);
            Assert.Equal("10,20,1280,720,0", s.ShellBounds);
            Assert.False(s.AnimateOnPress);
            Assert.Equal("L", s.SubtitleSize);
            Assert.False(s.RememberVolume);
            Assert.Equal(100, s.StartVolume); // Remember volume off: opens at 100, the stored 42 is kept
            // New in 3.0.0: unset in an old file, so they take their defaults.
            Assert.True(s.PreventSleep);
            Assert.Equal("auto", s.HardwareDecoding);
            Assert.False(warned);
            // A later write keeps the choices that still exist (including the 2.0.2-only keys).
            s.HardwareDecoding = "off";
            var again = new PlayerSettings(path);
            Assert.Equal("off", again.HardwareDecoding);
            Assert.Equal("D:\\Movies", again.LibraryFolder);
            Assert.Equal(15, again.PrimarySkipSeconds);
            Assert.Equal(120, again.SecondarySkipSeconds);
            Assert.Equal(42, again.LastVolume);
            Assert.Equal("10,20,1280,720,0", again.ShellBounds);
            Assert.False(again.AnimateOnPress);
            Assert.Equal("L", again.SubtitleSize);
            Assert.False(again.RememberVolume);
        }

        [Fact]
        public void Settings202_OldSkipKeysStillFeedTheSplitSettingsWhenTheNewKeysAreAbsent()
        {
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path, "{\"SkipBackSeconds\":15,\"SkipForwardSeconds\":60}");
            var s = new PlayerSettings(path);
            Assert.Equal(15, s.PrimarySkipSeconds);
            Assert.Equal(60, s.SecondarySkipSeconds);
        }

        [Fact]
        public void History202_ItemsWithoutPlayedUtcOrPathStillResumeButDoNotAppearInContinueWatching()
        {
            var movie = Path.Combine(_dir, "movie.mp4");
            File.WriteAllBytes(movie, new byte[4096]);
            var info = new FileInfo(movie);
            var key = movie.ToUpperInvariant();
            var json = JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                [key] = new { PositionMs = 600000L, DurationMs = 5400000L, FileSize = info.Length, ModifiedUtc = info.LastWriteTimeUtc }
            });
            var histPath = Path.Combine(_dir, "playback-history.json");
            File.WriteAllText(histPath, json);
            var h = new PlaybackHistory(histPath);
            var item = h.Get(movie);
            Assert.NotNull(item);
            Assert.Equal(600000L, item!.PositionMs);
            Assert.Equal(default(DateTime), item.PlayedUtc);
            Assert.Empty(h.Recent(5));
            // Playing it again records PlayedUtc and Path, and it then shows up.
            h.Save(movie, 900000L, 5400000L);
            Assert.Single(h.Recent(5));
            Assert.Equal(900000L, h.Get(movie)!.PositionMs);
        }
    }
}
