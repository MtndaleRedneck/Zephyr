using Xunit;

namespace ZephyrPlayer.Tests
{
    public class FullscreenBarWidthTests
    {
        [Theory]
        [InlineData(1920, 760)]
        [InlineData(3840, 760)]
        [InlineData(1400, 760)]
        [InlineData(800, 760)]
        [InlineData(700, 660)]
        [InlineData(400, 360)]
        public void Width_IsCappedAt760_ButNeverWiderThanAvailableMinusMargins(double available, double expected)
            => Assert.Equal(expected, MainWindow.BarWidth(available), 3);

        [Fact]
        public void Width_NeverNegative() => Assert.Equal(0, MainWindow.BarWidth(10));
    }
}
