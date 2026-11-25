using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>Resume rule and history-file safety, ported from the 2.0.2 behavior and extended.</summary>
    public class ResumeAndHistoryTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "zp-res-" + Guid.NewGuid().ToString("N"));
        public ResumeAndHistoryTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static PlaybackHistory.Item At(long positionMs, long durationMs) => new(positionMs, durationMs, 1, DateTime.UtcNow);

        [Fact]
        public void ResumeSeconds_Rules()
        {
            Assert.Null(PlaybackHistory.ResumeSeconds(null));
            Assert.Null(PlaybackHistory.ResumeSeconds(At(5_000, 3_600_000)));     // inside the first 10 s
            Assert.Null(PlaybackHistory.ResumeSeconds(At(9_999, 3_600_000)));
            Assert.Equal(10L, PlaybackHistory.ResumeSeconds(At(10_000, 3_600_000)));
            Assert.Equal(1800L, PlaybackHistory.ResumeSeconds(At(1_800_000, 3_600_000)));
            Assert.Null(PlaybackHistory.ResumeSeconds(At(3_420_000, 3_600_000))); // 95 % and later: start over
            Assert.Equal(3419L, PlaybackHistory.ResumeSeconds(At(3_419_999, 3_600_000)));
            Assert.Null(PlaybackHistory.ResumeSeconds(At(100_000, 0)));            // unknown duration
        }

        [Fact]
        public void SavedSpot_Resumes_Through_Get_And_ResumeSeconds()
        {
            var movie = Path.Combine(_dir, "movie.mp4");
            File.WriteAllBytes(movie, new byte[2048]);
            var h = new PlaybackHistory(Path.Combine(_dir, "playback-history.json"));
            h.Save(movie, 754_000, 5_400_000);
            Assert.Equal(754L, PlaybackHistory.ResumeSeconds(h.Get(new Uri(movie).AbsoluteUri)));
            // A file that changed on disk no longer resumes.
            File.WriteAllBytes(movie, new byte[4096]);
            Assert.Null(PlaybackHistory.ResumeSeconds(h.Get(movie)));
        }

        [Fact]
        public void UnreadableHistory_IsPreservedByteForByte_NotOverwritten()
        {
            var movie = Path.Combine(_dir, "movie.mp4");
            File.WriteAllBytes(movie, new byte[2048]);
            var hist = Path.Combine(_dir, "playback-history.json");
            var damaged = "{ not json \u00e9 ";
            File.WriteAllText(hist, damaged);
            var h = new PlaybackHistory(hist);
            Assert.Null(h.Get(movie));            // unreadable reads as empty
            h.Save(movie, 600_000, 5_400_000);    // the next save must not destroy the damaged file
            var aside = Directory.GetFiles(_dir, "playback-history.json.unreadable-*");
            Assert.Single(aside);
            Assert.Equal(damaged, File.ReadAllText(aside[0]));
            Assert.Equal(600L, PlaybackHistory.ResumeSeconds(h.Get(movie))); // and history works again
        }

        [Fact]
        public void MissingHistory_IsEmpty_AndFirstSaveCreatesIt_WithNothingMovedAside()
        {
            var movie = Path.Combine(_dir, "movie.mp4");
            File.WriteAllBytes(movie, new byte[2048]);
            var hist = Path.Combine(_dir, "playback-history.json");
            var h = new PlaybackHistory(hist);
            Assert.Null(h.Get(movie));
            h.Save(movie, 600_000, 5_400_000);
            Assert.True(File.Exists(hist));
            Assert.Empty(Directory.GetFiles(_dir, "playback-history.json.unreadable-*"));
        }
    }
}
