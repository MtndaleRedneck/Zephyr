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
        [Theory]
        [InlineData(0, "")][InlineData(90000, "1:30")][InlineData(3723000, "1:02:03")]
        public void DurationLabelsUseRealTime(long ms, string expected) => Assert.Equal(expected, PlaybackHistory.DurationLabel(ms));
    }
}
