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
            Core.Initialize();
            using var vlc = new LibVLC("--no-video", "--no-audio");
            using var media = new Media(vlc, Path.Combine(System.AppContext.BaseDirectory, "fixtures", "clip.mp4"), FromType.FromPath);
            await media.Parse(MediaParseOptions.ParseLocal, 1500);
            Assert.True(media.Duration > 0, "Library duration parse returned no real duration");
        }
    }
}
