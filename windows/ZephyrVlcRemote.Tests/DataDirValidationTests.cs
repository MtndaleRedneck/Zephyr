using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

public class DataDirValidationTests
{
    [Theory]
    [InlineData(@"C:\Users\a", @"C:\Users\a\AppData\Local\ZephyrVlcRemote", true)]
    [InlineData(@"C:\Users\a\", @"c:\users\A\appdata\local\x", true)]
    [InlineData(@"C:\Users\a", @"C:\Users\a", false)]
    [InlineData(@"C:\Users\a", @"C:\Users\ab\x", false)]
    [InlineData(@"C:\Users\a", @"C:\Users\a\..\b\x", false)]
    [InlineData(@"C:\Users\a", @"C:\Windows\System32", false)]
    [InlineData(@"C:\Users\a", @"relative\path", false)]
    [InlineData(@"C:\Users\a", @"", false)]
    public void PathMustBeStrictlyInsideTheProfile(string root, string dir, bool expected) =>
        Assert.Equal(expected, FirewallSetup.IsPathInside(root, dir));
}
