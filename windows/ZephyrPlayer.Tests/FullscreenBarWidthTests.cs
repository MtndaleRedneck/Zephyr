using Xunit;

namespace ZephyrPlayer.Tests
{
    public class FullscreenBarWidthTests
    {
        [Theory]
        [InlineData(1920, 960)]
        [InlineData(3840, 1100)]
        [InlineData(1400, 760)]
        [InlineData(800, 760)]
        [InlineData(700, 660)]
        [InlineData(400, 360)]
        public void Width_IsHalfClamped_ButNeverWiderThanAvailableMinusMargins(double available, double expected)
            => Assert.Equal(expected, MainWindow.FullscreenBarWidth(available), 3);

        [Fact]
        public void Width_NeverNegative() => Assert.Equal(0, MainWindow.FullscreenBarWidth(10));
    }
}
