using System;
using System.IO;
using System.Threading;
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
        [Fact] public void MetadataOnlySave_FillsInAMissingLength_AndKeepsTheSavedSpotAndPlayedTime()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-history-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
            var video = Path.Combine(dir, "movie.mp4"); File.WriteAllText(video, "video fixture");
            var file = Path.Combine(dir, "history.json");
            var f = new FileInfo(video); var played = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            // An old entry: a saved spot, no length (Save never writes this today; older data can).
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, PlaybackHistory.Item>
            { [video.ToUpperInvariant()] = new(50_000, 0, f.Length, f.LastWriteTimeUtc, played, video) }));
            var history = new PlaybackHistory(file);
            history.Save(video, 0, 100_000, metadataOnly: true);
            var got = history.Get(video)!;
            Assert.Equal(50_000, got.PositionMs);     // the saved spot is kept
            Assert.Equal(100_000, got.DurationMs);    // the length is filled in
            Assert.Equal(played, got.PlayedUtc);
            // A known length is left alone, and a later real save still moves the spot.
            history.Save(video, 0, 90_000, metadataOnly: true);
            Assert.Equal(100_000, history.Get(video)!.DurationMs);
            history.Save(video, 70_000, 100_000);
            Assert.Equal(70_000, history.Get(video)!.PositionMs);
            Directory.Delete(dir, true);
        }
        [Fact] public void Recent_ListsUnfinishedNewestFirst_AndSkipsFinishedChangedAndOutsideRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "zephyr-recent-" + Guid.NewGuid());
            var other = Path.Combine(Path.GetTempPath(), "zephyr-recent-other-" + Guid.NewGuid());
            Directory.CreateDirectory(root); Directory.CreateDirectory(other);
            string Make(string dir, string name) { var f = Path.Combine(dir, name); File.WriteAllText(f, name); return f; }
            var a = Make(root, "A Movie.mp4"); var b = Make(root, "B Movie.mp4"); var done = Make(root, "Done.mp4");
            var changed = Make(root, "Changed.mp4"); var early = Make(root, "Early.mp4"); var outside = Make(other, "Outside.mp4");
            var history = new PlaybackHistory(Path.Combine(root, "history.json"));
            history.Save(a, 60_000, 600_000); Thread.Sleep(20);
            history.Save(done, 590_000, 600_000); history.Save(changed, 60_000, 600_000);
            history.Save(early, 5_000, 600_000); history.Save(outside, 60_000, 600_000); Thread.Sleep(20);
            history.Save(b, 120_000, 600_000);
            File.AppendAllText(changed, "more");
            var recent = history.Recent(3, root);
            Assert.Equal(new[] { b, a }, recent.ConvertAll(r => r.Path).ToArray());
            Assert.Equal("8 min left", PlaybackHistory.TimeLeftLabel(120_000, 600_000));
            Assert.Equal("1 h 30 min left", PlaybackHistory.TimeLeftLabel(0, 90 * 60_000));
            Assert.Equal("1 min left", PlaybackHistory.TimeLeftLabel(599_000, 600_000));
            // Metadata-only updates never turn an unplayed file into a recent one or bump a played one.
            var fresh = Make(root, "Fresh.mp4"); history.Save(fresh, 0, 600_000, metadataOnly: true);
            Assert.DoesNotContain(history.Recent(5, root), r => r.Path == fresh);
            Directory.Delete(root, true); Directory.Delete(other, true);
        }
        [Fact] public void RecentCountZeroOrNegative_ReturnsNothing_EvenWithEligibleEntries()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-history-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
            var video = Path.Combine(dir, "eligible.mp4"); File.WriteAllText(video, "x");
            var history = new PlaybackHistory(Path.Combine(dir, "history.json")); history.Save(video, 60_000, 600_000);
            Assert.Single(history.Recent(1, dir)); // the entry really is eligible
            Assert.Empty(history.Recent(0, dir)); Assert.Empty(history.Recent(-1, dir));
            Directory.Delete(dir, true);
        }
        [Fact] public void RecentAcceptFilter_RunsBeforeCounting()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-history-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
            var history = new PlaybackHistory(Path.Combine(dir, "history.json"));
            var older = Path.Combine(dir, "older.mp4"); File.WriteAllText(older, "x"); history.Save(older, 60_000, 600_000); Thread.Sleep(20);
            for (var i = 0; i < 12; i++) { var f = Path.Combine(dir, $"note{i}.txt"); File.WriteAllText(f, "x"); history.Save(f, 60_000, 600_000); Thread.Sleep(5); }
            var got = history.Recent(3, dir, p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(new[] { older }, got.ConvertAll(r => r.Path).ToArray());
            Directory.Delete(dir, true);
        }
        [Fact] public void OldHistoryFilesWithoutPlayedTimeStillLoad()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-history-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
            var video = Path.Combine(dir, "old.mp4"); File.WriteAllText(video, "x"); var info = new FileInfo(video);
            var json = "{\"" + video.ToUpperInvariant().Replace("\\", "\\\\") + "\":{\"PositionMs\":5000,\"DurationMs\":10000,\"FileSize\":" + info.Length + ",\"ModifiedUtc\":\"" + info.LastWriteTimeUtc.ToString("O") + "\"}}";
            var file = Path.Combine(dir, "history.json"); File.WriteAllText(file, json);
            var history = new PlaybackHistory(file);
            Assert.Equal(5000, history.Get(video)!.PositionMs);
            Assert.Empty(history.Recent(3, dir));
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
namespace ZephyrPlayer.Tests
{
    public class LibraryCountWordsTests
    {
        [Theory] [InlineData(0, "0 folders")] [InlineData(1, "1 folder")] [InlineData(6, "6 folders")]
        public void Counts_Use_Singular_For_One(int n, string expected) => Assert.Equal(expected, HomeWindow.Plural(n, "folder"));
    }
}
