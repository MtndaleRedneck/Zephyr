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
        public void Defaults_On_Missing_File()
        {
            var s = new PlayerSettings(_path);
            Assert.Equal("", s.LibraryFolder);
            Assert.Equal(200, s.VolumeBoostMaxPct);
            Assert.Equal(5, s.SkipBackSeconds);
            Assert.Equal(30, s.SkipForwardSeconds);
            Assert.Equal("original", s.BarColorStyle);
            Assert.Equal("hollow", s.IconStyle);
            Assert.False(s.ShowSpeed);
            Assert.False(s.ShowSleepTimer);
            Assert.Equal(100, s.LastVolume);
        }

        [Fact]
        public void RoundTrips_All_Fields()
        {
            var s = new PlayerSettings(_path);
            s.LibraryFolder = @"C:\Movies";
            s.VolumeBoostMaxPct = 150;
            s.SkipBackSeconds = 10;
            s.SkipForwardSeconds = 60;
            s.BarColorStyle = "twotone";
            s.IconStyle = "filled";
            s.ShowSpeed = true;
            s.ShowSleepTimer = true;
            s.LastVolume = 75;
            var reread = new PlayerSettings(_path);
            Assert.Equal(@"C:\Movies", reread.LibraryFolder);
            Assert.Equal(150, reread.VolumeBoostMaxPct);
            Assert.Equal(10, reread.SkipBackSeconds);
            Assert.Equal(60, reread.SkipForwardSeconds);
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
            s.SkipBackSeconds = 7;
            Assert.Equal(5, s.SkipBackSeconds);
            s.BarColorStyle = "purple";
            Assert.Equal("original", s.BarColorStyle);
            s.IconStyle = "sparkly";
            Assert.Equal("hollow", s.IconStyle);
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
