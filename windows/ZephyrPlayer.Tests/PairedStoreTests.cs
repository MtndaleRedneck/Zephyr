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
        public void LockedStore_200BogusBearerChecksDoNotSleepOrRereadPerRequest()
        {
            File.WriteAllText(FilePath,"[\"legacy\"]");var store=new PairedStore(FilePath);
            using var locked=new FileStream(FilePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            var watch=System.Diagnostics.Stopwatch.StartNew();
            for(var i=0;i<200;i++)Assert.False(store.ValidateAndTouch("bogus-"+i));
            Assert.True(watch.Elapsed<TimeSpan.FromSeconds(1),"locked store blocked request admission");Assert.True(store.IsBroken);
        }
        [Fact]
        public void TransientLockedRead_DoesNotOfferDestructiveResetAndRecovers()
        {
            File.WriteAllText(FilePath,"[\"legacy\"]");var store=new PairedStore(FilePath);
            using(var locked=new FileStream(FilePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
                Assert.False(store.HasAny);Assert.True(store.IsBroken);Assert.False(store.CanResetUnreadable);
                Assert.Throws<InvalidOperationException>(()=>store.ResetUnreadablePairings());
            }
            System.Threading.Thread.Sleep(3100);Assert.True(store.IsPaired("legacy"));Assert.False(store.IsBroken);
        }
        [Fact]
        public void ValidLegacyRead_MigrationWriteFailureRetainsAuthentication()
        {
            var original="[\"legacy-token\"]";File.WriteAllText(FilePath,original);Directory.CreateDirectory(FilePath+".tmp");
            var store=new PairedStore(FilePath);Assert.True(store.IsPaired("legacy-token"));Assert.False(store.IsBroken);Assert.Equal(original,File.ReadAllText(FilePath));
            Directory.Delete(FilePath+".tmp");store.Add("new-token");Assert.True(new PairedStore(FilePath).IsPaired("legacy-token"));Assert.Contains("dpapi:",File.ReadAllText(FilePath));
        }
        [Fact]
        public void ExplicitBrokenStoreReset_QuarantinesOriginalThenAllowsRePair()
        {
            var original="[{\"token\":\"dpapi:broken\",\"name\":\"Phone\"}]";File.WriteAllText(FilePath,original);var store=new PairedStore(FilePath);Assert.False(store.HasAny);Assert.True(store.IsBroken);
            var quarantined=store.ResetUnreadablePairings();Assert.Equal(original,File.ReadAllText(quarantined));Assert.False(store.IsBroken);Assert.Empty(store.Entries());store.Add("repaired","Phone");Assert.True(store.IsPaired("repaired"));
            Assert.Throws<InvalidOperationException>(()=>store.ResetUnreadablePairings());
        }

        [Fact]
        public void SavedToken_IsProtected_AndRoundTrips()
        {
            var store = new PairedStore(FilePath);
            store.Add("private-pairing-token", "Phone");
            var stored = File.ReadAllText(FilePath);
            Assert.DoesNotContain("private-pairing-token", stored);
            Assert.Contains("dpapi:", stored);
            Assert.True(new PairedStore(FilePath).IsPaired("private-pairing-token"));
        }
        [Fact]
        public void UnreadableProtectedToken_FailsClosed_WithoutRewrite()
        {
            var json = "[{\"token\":\"dpapi:invalid\",\"name\":\"Phone\"}]";
            File.WriteAllText(FilePath, json);
            var store = new PairedStore(FilePath);
            Assert.False(store.HasAny);
            Assert.True(store.IsBroken);
            Assert.Throws<InvalidOperationException>(() => store.Add("new", "Phone"));
            Assert.Equal(json, File.ReadAllText(FilePath));
        }
        [Fact]
        public void Connected_Requires_Recent_Authenticated_Activity()
        {
            var store = new PairedStore(FilePath);
            store.Add("tok-new", "Phone");
            Assert.False(store.IsConnected("tok-new"));
            Assert.False(store.ValidateAndTouch("wrong"));
            Assert.True(store.ValidateAndTouch("tok-new"));
            Assert.True(store.IsConnected("tok-new"));
            Assert.NotNull(store.Entries()[0].PairedAt);
            store.Remove("tok-new");
            Assert.False(store.ValidateAndTouch("tok-new"));
        }
        [Fact]
        public void Unreadable_File_Fails_Closed_And_Never_Gets_Overwritten()
        {
            File.WriteAllText(FilePath, "{ this is not json ");
            var store = new PairedStore(FilePath);
            Assert.False(store.IsPaired("anything"));   // reads look unpaired
            Assert.True(store.IsBroken);
            Assert.Throws<InvalidOperationException>(() => store.Add("tok", "phone"));
            // The original file is untouched - pairing is never "fixed" by clobbering it.
            Assert.Equal("{ this is not json ", File.ReadAllText(FilePath));
        }

        [Fact]
        public void Broken_Store_Refused_Add_Leaves_No_Phantom_In_Memory()
        {
            // Round-3 P4: a refused write must not leak into the cached list -
            // IsPaired/Count/Entries stay exactly what the unreadable file says.
            File.WriteAllText(FilePath, "{ this is not json ");
            var store = new PairedStore(FilePath);
            Assert.Throws<InvalidOperationException>(() => store.Add("tok-phantom", "My phone"));
            Assert.Throws<InvalidOperationException>(() => store.Add("tok-phantom", "My phone"));
            Assert.False(store.IsPaired("tok-phantom"));
            Assert.Equal(0, store.Count);
            Assert.Empty(store.Entries());
        }

        [Fact]
        public void Broken_Store_Remove_Refuses_Before_Touching_Anything()
        {
            File.WriteAllText(FilePath, "{ this is not json ");
            var store = new PairedStore(FilePath);
            Assert.Throws<InvalidOperationException>(() => store.Remove("tok"));
            Assert.True(store.IsBroken);
            Assert.Equal("{ this is not json ", File.ReadAllText(FilePath));
        }

        [Fact]
        public void Failed_Write_Changes_Nothing_In_Memory_Or_On_Disk()
        {
            // Transactional (round-3 P4): the in-memory list only advances after a
            // successful persist, so a failing disk never creates a phantom pairing.
            var store = new PairedStore(FilePath);
            store.Add("tok-a", "First");
            // Make the directory read-only-ish: replace the target path's parent
            // with a FILE so the temp write cannot land.
            var dirFile = Path.Combine(Path.GetTempPath(), "zephyr-store-block-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(dirFile, "not a dir");
            var blocked = new PairedStore(Path.Combine(dirFile, "paired.json"));
            Assert.Throws<InvalidOperationException>(() => blocked.Add("tok-b", "Second"));
            Assert.False(blocked.IsPaired("tok-b"));
            Assert.Equal(0, blocked.Count);
            File.Delete(dirFile);
        }

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
            // Names default; successful legacy reads migrate tokens atomically.
            Assert.Equal(new[] { "My phone", "My phone" },
                store.Entries().ConvertAll(e => e.Name).ToArray());
            Assert.DoesNotContain("tok-a", File.ReadAllText(FilePath));
            Assert.True(new PairedStore(FilePath).IsPaired("tok-a"));
        }

        [Fact]
        public void Add_Persists_Names_And_Migrates_The_File()
        {
            File.WriteAllText(FilePath, "[\"tok-a\"]");
            var store = new PairedStore(FilePath);
            store.Add("tok-b", "Test phone");
            var entries = store.Entries();
            Assert.Equal(2, entries.Count);
            Assert.Equal("Test phone", entries[1].Name);
            // The file moved to the entry shape; a FRESH store reads it back.
            var reread = new PairedStore(FilePath);
            Assert.True(reread.IsPaired("tok-a"));
            Assert.Equal("Test phone", reread.Entries()[1].Name);
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
