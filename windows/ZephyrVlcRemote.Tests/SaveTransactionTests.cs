using System.Text.Json;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>Round-6 missing tests: the save transaction itself. Two concurrent pairing
/// transactions must serialize through the OUTER save lock (no lost phone, no torn
/// file), and a failed save must roll the in-memory add back so a retry after the
/// cause is fixed persists cleanly - no phantom pairing either way.</summary>
public class SaveTransactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-save-" + Guid.NewGuid().ToString("N"));

    public SaveTransactionTests()
    {
        Directory.CreateDirectory(_dir);
        Program.RedirectDataDirForTests(_dir);
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
    }

    public void Dispose()
    {
        Program.StoreBroken = false;
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string[] TokensOnDisk()
    {
        var json = File.ReadAllText(Program.ConfigPath);
        return JsonDocument.Parse(json).RootElement.GetProperty("phones")
            .EnumerateArray()
            .Select(p => Dpapi.Unwrap(p.GetProperty("token").GetString()!))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void ConcurrentPairTransactionsLoseNoPhoneAndNeverTearTheFile()
    {
        const int perThread = 25;
        var errors = new List<Exception>();
        void Worker(string tag)
        {
            try
            {
                for (var i = 0; i < perThread; i++)
                    Program.AddPhoneAndPersist("tok-" + tag + "-" + i, tag + " phone " + i);
            }
            catch (Exception ex) { lock (errors) { errors.Add(ex); } }
        }
        var a = new Thread(() => Worker("a"));
        var b = new Thread(() => Worker("b"));
        a.Start(); b.Start(); a.Join(); b.Join();

        Assert.Empty(errors);
        var expected = Enumerable.Range(0, perThread).Select(i => "tok-a-" + i)
            .Concat(Enumerable.Range(0, perThread).Select(i => "tok-b-" + i))
            .OrderBy(t => t, StringComparer.Ordinal).ToArray();
        // Every transaction persisted: the file holds exactly the union of both threads.
        Assert.Equal(expected, TokensOnDisk());
        // Memory and disk agree (no save finished out of order with a stale snapshot).
        Assert.Equal(expected, Program.PhonesSnapshot().Select(p => p.Token).OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void FailedSaveRollsBackInMemoryAndRetryPersists()
    {
        // Force the failure: a read-only existing config makes the atomic replace throw.
        Program.AddPhoneAndPersist("tok-first", "first phone");
        new FileInfo(Program.ConfigPath).IsReadOnly = true;
        try
        {
            Assert.ThrowsAny<Exception>(() => Program.AddPhoneAndPersist("tok-second", "second phone"));
            // Refused write leaves no phantom: the phone is gone from memory...
            Assert.DoesNotContain(Program.PhonesSnapshot(), p => p.Token == "tok-second");
            // ...and the file still holds exactly the first phone.
            Assert.Equal(new[] { "tok-first" }, TokensOnDisk());
        }
        finally
        {
            new FileInfo(Program.ConfigPath).IsReadOnly = false;
        }
        // Cause fixed: the retry persists and both phones are now on disk.
        Program.AddPhoneAndPersist("tok-second", "second phone");
        Assert.Equal(new[] { "tok-first", "tok-second" }, TokensOnDisk());
        Assert.Contains(Program.PhonesSnapshot(), p => p.Token == "tok-second");
    }

    [Fact]
    public void BrokenStoreRefusesSaveAndPreservesTheFile()
    {
        Program.AddPhoneAndPersist("tok-kept", "kept phone");
        var before = File.ReadAllBytes(Program.ConfigPath);
        Program.StoreBroken = true;
        Assert.Throws<InvalidOperationException>(() => Program.SaveConfig());
        // The preserved file is byte-identical: a torn store is never overwritten.
        Assert.Equal(before, File.ReadAllBytes(Program.ConfigPath));
    }
}
