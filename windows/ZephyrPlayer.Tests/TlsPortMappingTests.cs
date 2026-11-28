using Xunit;

namespace ZephyrPlayer.Tests
{
    public class TlsPortMappingTests
    {
        [Fact] public void MarkerPortAcceptance_PriorToCurrentOnly_AndCurrentMatchesTheServerDefault()
        {
            int prior = TlsProduct.PriorHttpsPort, cur = TlsProduct.CurrentHttpsPort;
            Assert.Equal(PlaybackServer.DefaultPort, cur);
            Assert.NotEqual(prior, cur);
            Assert.True(TlsPolicy.MarkerPortAcceptable(cur, cur, prior, cur));
            Assert.True(TlsPolicy.MarkerPortAcceptable(prior, cur, prior, cur));
            Assert.False(TlsPolicy.MarkerPortAcceptable(cur, prior, prior, cur));
            Assert.False(TlsPolicy.MarkerPortAcceptable(prior, cur + 1, prior, cur));
            Assert.False(TlsPolicy.MarkerPortAcceptable(12345, cur, prior, cur));
        }
    }
}
