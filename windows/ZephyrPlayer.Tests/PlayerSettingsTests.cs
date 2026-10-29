using System;
using System.IO;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>PlayerSettings: JSON persistence, defaults, validation, back-compat.</summary>
    public class PlayerSettingsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;
        public PlayerSettingsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "zephyr-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "settings.json");
        }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void CorruptSettings_ArePreservedAndNeverOverwrittenByDefaults() {
            var s = new PlayerSettings(_path); s.LastVolume = 37; s.LibraryFolder = "movies";
            Assert.True(File.Exists(_path + ".bak"));
            var good = File.ReadAllText(_path); File.WriteAllText(_path, "{broken");
            Assert.Equal(37, s.LastVolume);
            var warnings = 0; s.WriteFailed += (_, __) => warnings++;
            s.LastVolume = 70; s.LastVolume = 71;
            Assert.Equal(1, warnings);
            Assert.Equal("{broken", File.ReadAllText(_path));
            File.WriteAllText(_path, good); s.LastVolume = 70;
            Assert.Equal("movies", new PlayerSettings(_path).LibraryFolder);
        }
        [Fact]
        public void ButtonFlash_DefaultOn_ChoicePersists()
        {
            var s = new PlayerSettings(_path);
            Assert.True(s.ButtonFlash);
            s.ButtonFlash = false;
            Assert.False(new PlayerSettings(_path).ButtonFlash);
        }
        [Fact]
        public void Library_Sort_Roundtrips_Without_Losing_Older_Settings()
        {
            var settings = new PlayerSettings(_path);
            Assert.Equal("name", settings.LibrarySort); Assert.True(settings.FoldersFirst);
            settings.LibraryFolder = @"C:\Movies"; settings.LibrarySort = "created"; settings.FoldersFirst = false;
            var reread = new PlayerSettings(_path);
            Assert.Equal("created", reread.LibrarySort); Assert.False(reread.FoldersFirst); Assert.Equal(@"C:\Movies", reread.LibraryFolder);
        }
        [Fact]
        public void Defaults_On_Missing_File()
        {
            var s = new PlayerSettings(_path);
            Assert.Equal("", s.LibraryFolder);
            Assert.Equal(200, s.VolumeBoostMaxPct);
            Assert.Equal(10, s.PrimarySkipSeconds);
            Assert.Equal(30, s.SecondarySkipSeconds);
            Assert.Equal("orange", s.BarColorStyle); // unset default
            Assert.Equal("hollow", s.IconStyle);
            Assert.False(s.ShowSpeed);
            Assert.False(s.ShowSleepTimer);
            Assert.Equal(100, s.LastVolume);
        }

        [Fact]
        public void Stored_Original_Choice_Survives_The_Default_Change()
        {
            // The default moved to orange, but an explicit earlier pick is a stored
            // value, not a missing one: it must read back exactly as chosen.
            File.WriteAllText(_path, "{\"BarColorStyle\":\"original\"}");
            var s = new PlayerSettings(_path);
            Assert.Equal("original", s.BarColorStyle);
        }

        [Fact]
        public void RoundTrips_All_Fields()
        {
            var s = new PlayerSettings(_path);
            s.LibraryFolder = @"C:\Movies";
            s.VolumeBoostMaxPct = 150;
            s.PrimarySkipSeconds = 10;
            s.SecondarySkipSeconds = 60;
            s.BarColorStyle = "twotone";
            s.IconStyle = "filled";
            s.ShowSpeed = true;
            s.ShowSleepTimer = true;
            s.LastVolume = 75;
            var reread = new PlayerSettings(_path);
            Assert.Equal(@"C:\Movies", reread.LibraryFolder);
            Assert.Equal(150, reread.VolumeBoostMaxPct);
            Assert.Equal(10, reread.PrimarySkipSeconds);
            Assert.Equal(60, reread.SecondarySkipSeconds);
            Assert.Equal("twotone", reread.BarColorStyle);
            Assert.Equal("filled", reread.IconStyle);
            Assert.True(reread.ShowSpeed);
            Assert.True(reread.ShowSleepTimer);
            Assert.Equal(75, reread.LastVolume);
        }

        [Fact]
        public void Reads_Old_Single_Field_File()
        {
            // The first private builds wrote only LibraryFolder; upgrading must keep it.
            File.WriteAllText(_path, "{\"LibraryFolder\":\"C:\\\\Movies\"}");
            var s = new PlayerSettings(_path);
            Assert.Equal(@"C:\Movies", s.LibraryFolder);
            Assert.Equal(200, s.VolumeBoostMaxPct);
        }

        [Fact]
        public void Invalid_Choices_Are_Ignored()
        {
            var s = new PlayerSettings(_path);
            s.VolumeBoostMaxPct = 137;
            Assert.Equal(200, s.VolumeBoostMaxPct);
            s.PrimarySkipSeconds = 7;
            Assert.Equal(10, s.PrimarySkipSeconds);
            s.BarColorStyle = "purple";
            Assert.Equal("orange", s.BarColorStyle); // unset default
            s.IconStyle = "sparkly";
            Assert.Equal("hollow", s.IconStyle);
        }

        [Fact]
        public void Legacy_Directional_Values_Seed_Pairs_Without_Rewriting_File()
        {
            var json = "{\"SkipBackSeconds\":10,\"SkipForwardSeconds\":60,\"LastVolume\":47}";
            File.WriteAllText(_path, json);
            var s = new PlayerSettings(_path);
            Assert.Equal(10, s.PrimarySkipSeconds);
            Assert.Equal(60, s.SecondarySkipSeconds);
            Assert.Equal(json, File.ReadAllText(_path));
            s.PrimarySkipSeconds = 15;
            s.SecondarySkipSeconds = 30;
            var reread = new PlayerSettings(_path);
            Assert.Equal(15, reread.PrimarySkipSeconds);
            Assert.Equal(30, reread.SecondarySkipSeconds);
            Assert.Equal(47, reread.LastVolume);
        }

        [Fact]
        public void Both_Directions_Use_The_Same_Interval_Per_Pair()
        {
            var s = new PlayerSettings(_path);
            s.PrimarySkipSeconds = 10;
            s.SecondarySkipSeconds = 60;
            Assert.Equal(-10, s.SkipDelta(false, true));
            Assert.Equal(10, s.SkipDelta(false, false));
            Assert.Equal(-60, s.SkipDelta(true, true));
            Assert.Equal(60, s.SkipDelta(true, false));
        }

        [Fact]
        public void Invalid_Stored_Intervals_Use_Pair_Defaults()
        {
            File.WriteAllText(_path, "{\"PrimarySkipSeconds\":-10,\"SecondarySkipSeconds\":999}");
            var s = new PlayerSettings(_path);
            Assert.Equal(10, s.PrimarySkipSeconds);
            Assert.Equal(30, s.SecondarySkipSeconds);
        }

        [Fact]
        public void Choices_Match_Owners_Two_Distinct_Rows()
        {
            Assert.Equal(new[] { 5, 10, 15, 30 }, PlayerSettings.PrimarySkipChoices);
            Assert.Equal(new[] { 15, 30, 60, 120, 180 }, PlayerSettings.SecondarySkipChoices);
            var s = new PlayerSettings(_path);
            s.PrimarySkipSeconds = 60;
            s.SecondarySkipSeconds = 5;
            Assert.Equal(10, s.PrimarySkipSeconds);
            Assert.Equal(30, s.SecondarySkipSeconds);
            foreach (var value in PlayerSettings.SecondarySkipChoices)
            {
                s.SecondarySkipSeconds = value;
                Assert.Equal(value, new PlayerSettings(_path).SecondarySkipSeconds);
                Assert.Equal(-value, s.SkipDelta(true, true));
                Assert.Equal(value, s.SkipDelta(true, false));
            }
        }

        [Fact]
        public void Valid_Saved_Primary_Five_Seconds_Is_Not_Reset_To_New_Default()
        {
            File.WriteAllText(_path, "{\"PrimarySkipSeconds\":5,\"SecondarySkipSeconds\":120}");
            var s = new PlayerSettings(_path);
            Assert.Equal(5, s.PrimarySkipSeconds);
            Assert.Equal(120, s.SecondarySkipSeconds);
            File.WriteAllText(_path, "{\"SkipBackSeconds\":5,\"SkipForwardSeconds\":30}");
            Assert.Equal(5, s.PrimarySkipSeconds);
            Assert.Equal(30, s.SecondarySkipSeconds);
        }

        [Fact]
        public void LastVolume_Is_Clamped()
        {
            var s = new PlayerSettings(_path);
            s.LastVolume = 140;
            Assert.Equal(100, s.LastVolume);
            s.LastVolume = -5;
            Assert.Equal(0, s.LastVolume);
        }

        [Fact]
        public void Changed_Fires_On_Write()
        {
            var s = new PlayerSettings(_path);
            var fired = 0;
            s.Changed += (_, __) => fired++;
            s.ShowSpeed = true;
            s.LastVolume = 33;
            Assert.Equal(2, fired);
        }
    }
}
