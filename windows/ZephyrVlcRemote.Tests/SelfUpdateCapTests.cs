using System.Net;
using System.Text;
using Xunit;

namespace ZephyrVlcRemote.Tests;

/// <summary>Updater byte-cap behavior against a loopback feed: an oversized
/// installer is refused, and the partial download (file AND directory) is
/// removed, not left for the next-day sweep. Mutates the process-wide cap, so
/// it shares the ProgramState serialization.</summary>
[Collection("ProgramState")]
public class SelfUpdateCapTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _base;
    private byte[] _exeBytes = Array.Empty<byte>();

    public SelfUpdateCapTests()
    {
        var port = 47897;
        _base = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                var bytes = _exeBytes;
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() { try { _listener.Stop(); } catch { } }

    private static string UpdatesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZephyrVlcRemote", "Updates");

    private static string[] UpdateDirs() => Directory.Exists(UpdatesRoot) ? Directory.GetDirectories(UpdatesRoot) : new string[0];

    [Fact]
    public async Task Download_DeclaredLengthOverCap_RefusedAndCleansUp()
    {
        var oldLimit = SelfUpdate.MaxInstallerBytes;
        SelfUpdate.MaxInstallerBytes = 100;
        try
        {
            _exeBytes = new byte[200];
            var before = UpdateDirs();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SelfUpdate.Download("1.4.0", _base + "/exe", new string('b', 64), hostAllowed: _ => true));
            Assert.Contains("too large", ex.Message);
            Assert.Empty(UpdateDirs().Except(before)); // no partial download directory survives
        }
        finally { SelfUpdate.MaxInstallerBytes = oldLimit; }
    }

    [Fact]
    public async Task Download_BodyGrowsPastCap_DeletesAndThrows()
    {
        var oldLimit = SelfUpdate.MaxInstallerBytes;
        SelfUpdate.MaxInstallerBytes = 100;
        try
        {
            _exeBytes = new byte[64 * 1024 + 200]; // chunk larger than the cap: the running-total check fires
            var before = UpdateDirs();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SelfUpdate.Download("1.4.0", _base + "/exe", new string('b', 64), hostAllowed: _ => true));
            var survivors = UpdateDirs().Except(before).ToArray();
            Assert.Empty(survivors); // the partial file's directory is gone...
            foreach (var dir in survivors)
                Assert.False(File.Exists(Path.Combine(dir, "installer.exe"))); // ...and no partial installer.exe remains
        }
        finally { SelfUpdate.MaxInstallerBytes = oldLimit; }
    }
}
