using System.Windows;
using Xunit;

namespace ZephyrPlayer.Tests
{
    public class WindowGeometryTests
    {
        private static readonly Rect Screen = new(0, 0, 1920, 1080);

        [Fact]
        public void Format_And_Parse_RoundTrip()
        {
            var text = WindowGeometry.FormatBounds(new Rect(10.4, 20.6, 1280, 720), true);
            Assert.Equal("10,21,1280,720,1", text);
            var parsed = WindowGeometry.ParseBounds(text);
            Assert.NotNull(parsed);
            Assert.Equal(new Rect(10, 21, 1280, 720), parsed!.Value.Bounds);
            Assert.True(parsed.Value.Maximized);
            Assert.False(WindowGeometry.ParseBounds("1,2,3,4,0").HasValue);        // too small
            Assert.False(WindowGeometry.ParseBounds("a,b,c,d,0").HasValue);
            Assert.False(WindowGeometry.ParseBounds("").HasValue);
            Assert.False(WindowGeometry.ParseBounds("1,2,800,600").HasValue);       // wrong part count
        }

        [Fact]
        public void ChooseBounds_UsesSavedRect_OnlyWhenItLandsOnTheDesktop()
        {
            Assert.Equal(new Rect(100, 50, 1200, 800), WindowGeometry.ChooseBounds(new Rect(100, 50, 1200, 800), Screen));
            Assert.Null(WindowGeometry.ChooseBounds(new Rect(5000, 50, 1200, 800), Screen)); // monitor gone
            Assert.Null(WindowGeometry.ChooseBounds(null, Screen));
            Assert.Equal(1920, WindowGeometry.ChooseBounds(new Rect(0, 0, 4000, 800), Screen)!.Value.Width); // clamped
        }
    }
}
