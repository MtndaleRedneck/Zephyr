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
        // 2.0.2 Ver201RenderTests.RememberVolume_IsASwitch_OffStartsAt100, original assertions.
        [Fact]
        public void RememberVolume_IsASwitch_OffStartsAt100()
        {
            var s = new PlayerSettings(_path);
            Assert.True(s.RememberVolume);
            s.LastVolume = 37;
            Assert.Equal(37, s.StartVolume);
            s.RememberVolume = false;
            Assert.Equal(100, s.StartVolume);
            Assert.Equal(37, new PlayerSettings(_path).LastVolume);
            Assert.False(new PlayerSettings(_path).RememberVolume);
        }
        [Fact]
        public void ButtonFlash_DefaultOff_ChoicePersists()
        {
            var s = new PlayerSettings(_path);
            Assert.False(s.ButtonFlash);
            s.ButtonFlash = true;
            Assert.True(new PlayerSettings(_path).ButtonFlash);
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
            Assert.Equal("original", s.BarColorStyle); // unset default (2.0.2)
            Assert.Equal("hollow", s.IconStyle);
            Assert.True(s.ShowSpeed);
            Assert.True(s.ShowSleepTimer);
            Assert.Equal(100, s.LastVolume);
            Assert.True(s.AnimateOnPress);
            Assert.Equal("M", s.SubtitleSize);
            Assert.True(s.RememberVolume);
            Assert.Equal("", s.ShellBounds);
            Assert.Equal(100, s.StartVolume);
        }

        [Fact]
        public void Stored_Choices_Win_Over_Defaults()
        {
            // A stored choice is never a missing value: it reads back exactly as chosen.
            File.WriteAllText(_path, "{\"BarColorStyle\":\"orange\",\"ShowSpeed\":false,\"RememberVolume\":false,\"LastVolume\":42,\"SubtitleSize\":\"L\"}");
            var s = new PlayerSettings(_path);
            Assert.Equal("orange", s.BarColorStyle);
            Assert.False(s.ShowSpeed);
            Assert.False(s.RememberVolume);
            Assert.Equal(42, s.LastVolume);
            Assert.Equal(100, s.StartVolume); // Remember volume off: always opens at 100
            Assert.Equal("L", s.SubtitleSize);
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
            Assert.Equal("original", s.BarColorStyle); // unset default (2.0.2)
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
            s.ShowSpeed = false;
            Assert.Equal(1, fired);
            // 2.0.2: the remembered volume is written without Changed (nothing else shows it,
            // so no screen rebuilds per volume step), and it is still saved.
            s.LastVolume = 33;
            Assert.Equal(1, fired);
            Assert.Equal(33, new PlayerSettings(_path).LastVolume);
        }
    }
}

namespace ZephyrPlayer.Tests
{
    public class HardwareAndSleepSettingTests
    {
        [Fact]
        public void Defaults_AreAutoAndPreventSleepOn_AndRoundTrip()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid() + ".json");
            try
            {
                var s = new PlayerSettings(path);
                Assert.Equal("auto", s.HardwareDecoding);
                Assert.True(s.PreventSleep);
                s.HardwareDecoding = "off"; s.PreventSleep = false; s.HardwareDecoding = "bogus";
                var again = new PlayerSettings(path);
                Assert.Equal("off", again.HardwareDecoding);
                Assert.False(again.PreventSleep);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void Options_AutoAddsNothing_OffDisablesAvcodecHardware()
        {
            Assert.Empty(HardwareDecoding.Options("auto"));
            Assert.Equal(new[] { "--avcodec-hw=none" }, HardwareDecoding.Options("off"));
        }
    
        [Fact]
        public void AnimateOnPress_DefaultOn_And_SubtitleSize_DefaultM()
        {
            var s = new PlayerSettings(_path);
            Assert.True(s.AnimateOnPress);
            Assert.Equal("M", s.SubtitleSize);
            s.AnimateOnPress = false;
            s.SubtitleSize = "L";
            s.SubtitleSize = "XL"; // ignored
            var reread = new PlayerSettings(_path);
            Assert.False(reread.AnimateOnPress);
            Assert.Equal("L", reread.SubtitleSize);
        }

        [Fact]
        public void Stored_Orange_Choice_Survives_The_Default_Change()
        {
            // The default is "original" again (render 06b), but an explicit earlier pick
            // is a stored value, not a missing one: it must read back exactly as chosen.
            File.WriteAllText(_path, "{\"BarColorStyle\":\"orange\"}");
            var s = new PlayerSettings(_path);
            Assert.Equal("orange", s.BarColorStyle);
        }

        [Fact]
        public void Stored_Speed_And_Sleep_Choices_Survive_The_Default_Change()
        {
            // U8: the new "on" default applies only when the key was never written.
            File.WriteAllText(_path, "{\"ShowSpeed\":false,\"ShowSleepTimer\":false}");
            var s = new PlayerSettings(_path);
            Assert.False(s.ShowSpeed);
            Assert.False(s.ShowSleepTimer);
        }

        [Fact]
        public void WriteFailure_IsReported_Once_AndNeverThrows()
        {
            // The settings "file" path is a directory: every write fails with an I/O error.
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-202-savefail-" + Guid.NewGuid().ToString("N"));
            var blocked = Path.Combine(dir, "settings.json");
            Directory.CreateDirectory(blocked);
            try
            {
                var s = new PlayerSettings(blocked);
                var failed = new System.Collections.Generic.List<Exception>();
                var changed = 0;
                s.SaveFailed += (_, ex) => failed.Add(ex);
                s.Changed += (_, __) => changed++;
                var ex1 = Record.Exception(() => { s.ShowSpeed = true; s.LastVolume = 40; s.LibraryFolder = "x"; });
                Assert.Null(ex1);
                Assert.Single(failed);
                Assert.Equal(0, changed);
                // A file that can be written again clears the warning; the next failure is said again.
                Directory.Delete(blocked);
                s.ShowSpeed = true;
                Assert.Equal(1, changed);
                File.Delete(blocked);
                Directory.CreateDirectory(blocked);
                s.ShowSpeed = false;
                Assert.Equal(2, failed.Count);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
