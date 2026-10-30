using System.IO;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Xunit;
namespace ZephyrPlayer.Tests
{
    public class LibraryDurationTests
    {
        [Fact] public async Task RealFixtureHasParsedDurationWithoutPlaying()
        {
            // CI run 41033075025: one native-library load failed here while other test
            // classes initialized libVLC in parallel; a second attempt is the same load.
            try { Core.Initialize(); }
            catch (VLCException) { await Task.Delay(500); Core.Initialize(); }
            using var vlc = new LibVLC("--no-video", "--no-audio");
            using var media = new Media(vlc, Path.Combine(System.AppContext.BaseDirectory, "fixtures", "clip.mp4"), FromType.FromPath);
            await media.Parse(MediaParseOptions.ParseLocal, 1500);
            Assert.True(media.Duration > 0, "Library duration parse returned no real duration");
        }
    }
}
