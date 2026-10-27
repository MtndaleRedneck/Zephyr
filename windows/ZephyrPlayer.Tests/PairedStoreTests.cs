using System;
using System.IO;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// The 1.2.0 store shape: tokens carry names for the Pairing screen, and a
    /// 1.1.0 string-array file still reads (upgrade never forces a re-pair).
    /// </summary>
    public class PairedStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "zephyr-store-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "paired.json");

        public PairedStoreTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Old_String_Array_Still_Reads_As_Paired()
        {
            File.WriteAllText(FilePath, "[\"tok-a\",\"tok-b\"]");
            var store = new PairedStore(FilePath);
            Assert.True(store.HasAny);
            Assert.Equal(2, store.Count);
            Assert.True(store.IsPaired("tok-a"));
            Assert.True(store.IsPaired("tok-b"));
            Assert.False(store.IsPaired("tok-c"));
            // Names default for the Pairing screen; reading never rewrites the file.
            Assert.Equal(new[] { "My phone", "My phone" },
                store.Entries().ConvertAll(e => e.Name).ToArray());
            Assert.Equal("[\"tok-a\",\"tok-b\"]", File.ReadAllText(FilePath));
        }

        [Fact]
        public void Add_Persists_Names_And_Migrates_The_File()
        {
            File.WriteAllText(FilePath, "[\"tok-a\"]");
            var store = new PairedStore(FilePath);
            store.Add("tok-b", "Ezzy's phone");
            var entries = store.Entries();
            Assert.Equal(2, entries.Count);
            Assert.Equal("Ezzy's phone", entries[1].Name);
            // The file moved to the entry shape; a FRESH store reads it back.
            var reread = new PairedStore(FilePath);
            Assert.True(reread.IsPaired("tok-a"));
            Assert.Equal("Ezzy's phone", reread.Entries()[1].Name);
        }

        [Fact]
        public void Remove_Forgets_Only_That_Phone()
        {
            var store = new PairedStore(FilePath);
            store.Add("tok-a", "One");
            store.Add("tok-b", "Two");
            store.Remove("tok-a");
            Assert.False(store.IsPaired("tok-a"));
            Assert.True(store.IsPaired("tok-b"));
            Assert.Single(store.Entries());
        }

        [Fact]
        public void Unreadable_Store_Fails_Closed()
        {
            File.WriteAllText(FilePath, "not json {{{");
            var store = new PairedStore(FilePath);
            Assert.False(store.HasAny);
            Assert.False(store.IsPaired("anything"));
            Assert.Equal(0, store.Count);
        }
    }
}
