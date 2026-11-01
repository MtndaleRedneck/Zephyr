using System;
using System.IO;
using Xunit;
namespace ZephyrPlayer.Tests
{
    public class PlaybackHistoryTests
    {
        [Fact] public void LocalProgressPersistsAndChangedFilesInvalidate()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-history-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var video = Path.Combine(dir, "movie.mp4");
            File.WriteAllText(video, "video fixture");
            var history = new PlaybackHistory(Path.Combine(dir, "history.json"));
            history.Save(new Uri(video).AbsoluteUri, 20000, 100000);
            Assert.Equal(20000, history.Get(video)!.PositionMs);
            history.Save(video, 0, 100000, metadataOnly: true); // late metadata parse cannot erase watched progress
            Assert.Equal(20000, history.Get(video)!.PositionMs);
            history.Save(video, 0, 100000);
            Assert.Equal(0, history.Get(video)!.PositionMs);
            File.AppendAllText(video, "changed");
            Assert.Null(history.Get(video));
            Directory.Delete(dir, true);
        }
        [Fact] public void RemoteAddressesNeverPersist()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-history-" + Guid.NewGuid());
            var file = Path.Combine(dir, "history.json");
            new PlaybackHistory(file).Save("http://192.168.1.1/token/movie", 10, 20);
            Assert.False(File.Exists(file));
        }
        // 2.0.2: a library start resumes from the saved spot, except in the first 10 s or
        // past 95 % of the movie (then it starts from 0:00).
        [Theory]
        [InlineData(0L, 0L, null)]                 // no length known
        [InlineData(5000L, 6000000L, null)]        // 0:05 in: nothing to resume
        [InlineData(10000L, 6000000L, 10L)]        // 0:10 in: resumes
        [InlineData(2530000L, 6000000L, 2530L)]    // 42:10 of 1:40:00
        [InlineData(5699000L, 6000000L, 5699L)]    // just under 95 %
        [InlineData(5700000L, 6000000L, null)]     // exactly 95 %: from 0:00
        [InlineData(5990000L, 6000000L, null)]     // the credits: from 0:00
        [InlineData(6000000L, 6000000L, null)]     // watched to the end
        public void LibraryResume_SavedSpot_ExceptTheStartAndTheLastFivePercent(long positionMs, long durationMs, long? expected)
            => Assert.Equal(expected, PlaybackHistory.ResumeSeconds(new PlaybackHistory.Item(positionMs, durationMs, 1, DateTime.UtcNow)));
        [Fact] public void LibraryResume_NoHistory_StartsFromTheBeginning()
        {
            Assert.Null(PlaybackHistory.ResumeSeconds(null));
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-history-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var video = Path.Combine(dir, "movie.mp4");
            File.WriteAllText(video, "video fixture");
            var history = new PlaybackHistory(Path.Combine(dir, "history.json"));
            Assert.Null(PlaybackHistory.ResumeSeconds(history.Get(new Uri(video).AbsoluteUri)));
            history.Save(new Uri(video).AbsoluteUri, 2530000, 6000000);
            Assert.Equal(2530L, PlaybackHistory.ResumeSeconds(history.Get(new Uri(video).AbsoluteUri)));
            Directory.Delete(dir, true);
        }
        [Theory]
        [InlineData(0, "")][InlineData(90000, "1:30")][InlineData(3723000, "1:02:03")]
        public void DurationLabelsUseRealTime(long ms, string expected) => Assert.Equal(expected, PlaybackHistory.DurationLabel(ms));
    }
}
