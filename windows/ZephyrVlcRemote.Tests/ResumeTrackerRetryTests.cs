using Xunit;

namespace ZephyrVlcRemote.Tests;

/// <summary>Round-8 N-6: the retry merge fills gaps only. A position recorded in memory
/// during the read-only window is newer and wins; a clear in that window is tombstoned
/// and the disk value must not come back.</summary>
[Collection("ProgramState")]
public class ResumeTrackerRetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zeph-resume-" + Guid.NewGuid().ToString("N"));

    public ResumeTrackerRetryTests()
    {
        Directory.CreateDirectory(_dir);
        ResumeTracker.ResetForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void WriteStore(string json) => File.WriteAllText(Path.Combine(_dir, "resume-places.json"), json);

    [Fact]
    public void UnreadableFileStaysReadOnlyAndInPlace()
    {
        WriteStore("{ not json");
        ResumeTracker.Start();
        Assert.True(ResumeTracker.SaveSuppressedForTests);
        // The file is exactly as it was: never moved, renamed or overwritten.
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(_dir, "resume-places.json")));
    }

    [Fact]
    public void RetryFillsGapsButKeepsNewerMemoryPositions()
    {
        WriteStore("{ not json");
        ResumeTracker.Start();
        Assert.True(ResumeTracker.SaveSuppressedForTests);
        // While read-only, a new place is recorded in memory (newer than anything on disk).
        ResumeTracker.RecordForTests(@"M:\movies\b.mp4", 300, 10000);
        // The file recovers with an OLDER position for b and a place memory never saw.
        WriteStore("{\"M:\\\\movies\\\\b.mp4\":{\"Position\":100,\"Length\":10000},\"M:\\\\movies\\\\c.mp4\":{\"Position\":200,\"Length\":10000}}");
        Assert.True(ResumeTracker.RetryNowForTests());
        Assert.False(ResumeTracker.SaveSuppressedForTests);
        Assert.Equal((300L, 10000L), ResumeTracker.Ask(@"M:\movies\b.mp4")); // memory wins
        Assert.Equal((200L, 10000L), ResumeTracker.Ask(@"M:\movies\c.mp4")); // gap filled
    }

    [Fact]
    public void ClearedDuringReadOnlyIsNotResurrected()
    {
        WriteStore("{ not json");
        ResumeTracker.Start();
        ResumeTracker.RecordForTests(@"M:\movies\a.mp4", 500, 10000);
        ResumeTracker.Clear(@"M:\movies\a.mp4"); // cleared during the read-only window
        // Disk recovers still holding a - the tombstone must keep it out.
        WriteStore("{\"M:\\\\movies\\\\a.mp4\":{\"Position\":500,\"Length\":10000}}");
        Assert.True(ResumeTracker.RetryNowForTests());
        Assert.Null(ResumeTracker.Ask(@"M:\movies\a.mp4"));
    }
}
