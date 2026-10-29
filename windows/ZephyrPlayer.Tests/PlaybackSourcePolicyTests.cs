using System;
using System.IO;
using System.Linq;
using System.Net;
using Xunit;
namespace ZephyrPlayer.Tests;
public class PlaybackSourcePolicyTests
{
    [Theory]
    [InlineData("smb://10.0.0.5/share/movie.mp4")]
    [InlineData("file://10.0.0.5/share/movie.mp4")]
    [InlineData("http://8.8.8.8:1234/s/tok/1")]
    [InlineData("http://10.0.0.9:1234/s/tok/1")]
    [InlineData("http://name.local:1234/s/tok/1")]
    [InlineData("http://u:p@10.0.0.5:1234/s/tok/1")]
    [InlineData("http://10.0.0.5:1234/playlist.m3u")]
    [InlineData("ftp://10.0.0.5/s/tok/1")]
    [InlineData("http://10.0.0.5:80/s/tok/1")]
    [InlineData("http://[::1]:1234/s/tok/1")]
    [InlineData("http://10.0.0.5:1234/s/tok/1?x=y")]
    [InlineData("http://10.0.0.5:1234/s/tok/1#x")]
    [InlineData("http://10.0.0.5:65536/s/tok/1")]
    public void UnsafeSourcesRejected(string source) => Assert.False(PlaybackSourcePolicy.Allowed(source, "", IPAddress.Parse("10.0.0.5")));
    [Theory]
    [InlineData("file:///C:/movies/a.mp4:payload")]
    [InlineData("file://localhost/C:/movies/a.mp4")]
    [InlineData("file:////host/share/a.mp4")]
    [InlineData("http://10.0.0.5:1234/s/tok/1%2fsub%2f0")]
    [InlineData("http://10.0.0.5:1234/s/tok/1\n")]
    public void DeviceAuthorityAlternateStreamAndEncodedRouteRejected(string source)=>Assert.False(PlaybackSourcePolicy.Allowed(source,@"C:\movies",IPAddress.Parse("10.0.0.5")));
    [Fact]
    public void IPv4MappedPeer_IsNormalizedWithoutAllowingOtherIPv6()
    {
        Assert.True(PlaybackSourcePolicy.Allowed("http://10.0.0.5:1234/s/tok/1","",IPAddress.Parse("::ffff:10.0.0.5")));
        Assert.True(PlaybackServer.IsPrivateAddress(IPAddress.Parse("::ffff:10.0.0.5")));
        Assert.False(PlaybackServer.IsPrivateAddress(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.False(PlaybackServer.IsPrivateAddress(IPAddress.Parse("2001:db8::1")));
    }
    [Fact]
    public void SubtitleCountBound_AndPeerMismatch()
    {
        var source="http://10.0.0.5:1234/s/tok/1";var subs=System.Linq.Enumerable.Repeat(source+"/sub/0",17).ToArray();
        Assert.False(PlaybackSourcePolicy.AllowedSet(source,subs,"",IPAddress.Parse("10.0.0.5")));
        Assert.False(PlaybackSourcePolicy.AllowedSet(source,new[]{"http://10.0.0.6:1234/s/tok/1/sub/0"},"",IPAddress.Parse("10.0.0.5")));
    }
    [Fact]
    public void PhoneStreamAndSubtitleAccepted()
    {
        Assert.True(PlaybackSourcePolicy.AllowedSet("http://10.0.0.5:1234/s/tok/1", new[] {"http://10.0.0.5:1234/s/tok/1/sub/0"}, "", IPAddress.Parse("10.0.0.5")));
    }
    [Fact]
    public void LocalLibraryFileAccepted_OutsideRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            var file = Path.Combine(dir, "Movie with spaces.mp4"); File.WriteAllText(file, "fixture");
            Assert.True(PlaybackSourcePolicy.Allowed(new Uri(file).AbsoluteUri, dir, IPAddress.Loopback));
            Assert.False(PlaybackSourcePolicy.Allowed(new Uri(file).AbsoluteUri, Path.Combine(dir,"other"), IPAddress.Loopback));
        } finally { Directory.Delete(dir,true); }
    }
}
