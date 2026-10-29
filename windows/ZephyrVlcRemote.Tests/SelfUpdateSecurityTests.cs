using System.Reflection;
using System.Security.Cryptography;
using Xunit;
namespace ZephyrVlcRemote.Tests;
public class SelfUpdateSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-sha")]
    public async Task InvalidDigestRefusedBeforeNetwork(string? digest)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(()=>SelfUpdate.Download("x","https://invalid.example/installer.exe",digest));
    }
    [Fact]
    public void ModifiedInstallerRefusedBeforeLaunch()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".exe");File.WriteAllText(path,"downloaded bytes");
        var map=(System.Collections.Concurrent.ConcurrentDictionary<string,string>)typeof(SelfUpdate).GetField("Verified",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        map[path]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        File.WriteAllText(path,"tampered bytes");
        Assert.Throws<InvalidOperationException>(()=>SelfUpdate.OpenVerifiedForLaunch(path));
        Assert.False(map.ContainsKey(path));try{File.Delete(path);}catch{}
    }
    [Fact]
    public void LaunchVerificationHandleDeniesWriteAndDelete()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".exe");File.WriteAllText(path,"downloaded bytes");
        var map=(System.Collections.Concurrent.ConcurrentDictionary<string,string>)typeof(SelfUpdate).GetField("Verified",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        map[path]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try{using var locked=SelfUpdate.OpenVerifiedForLaunch(path);Assert.Throws<IOException>(()=>File.WriteAllText(path,"tamper"));Assert.Throws<IOException>(()=>File.Delete(path));}
        finally{map.TryRemove(path,out _);File.Delete(path);}
    }
}
