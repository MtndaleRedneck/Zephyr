using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ZephyrPlayer.Tests;

/// <summary>A loopback movie server that sends the first 4096 real bytes of clip.mkv (so SafeMediaSource sniffs "mkv"
/// and libVLC starts reading) and then holds every read until released: no further bytes, no Playing, no error.
/// This is the "source nobody reads" a stall or a still-waiting start needs; a file always reaches EOF.</summary>
internal sealed class StalledMovieServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ManualResetEventSlim _release = new(false);
    private readonly byte[] _header;
    private const long Total = 4L * 1024 * 1024;
    internal string Url { get; }
    internal int Requests;

    internal StalledMovieServer()
    {
        var all = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mkv"));
        _header = new byte[4096]; Array.Copy(all, _header, _header.Length);
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Url = $"http://127.0.0.1:{port}/s/stall/1.mkv";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { break; }
                var n = Interlocked.Increment(ref Requests);
                _ = Task.Run(() => Serve(ctx, n));
            }
        });
    }

    private void Serve(HttpListenerContext ctx, int n)
    {
        try
        {
            var range = ctx.Request.Headers["Range"];
            var from = 0L;
            if (range != null && range.StartsWith("bytes=")) long.TryParse(range.Substring(6).Split('-')[0], out from);
            if (from == 0)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = Total;
                ctx.Response.OutputStream.Write(_header, 0, _header.Length);
                ctx.Response.OutputStream.Flush();
            }
            else
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {from}-{Total - 1}/{Total}";
                ctx.Response.ContentLength64 = Total - from;
                ctx.Response.OutputStream.Flush();
            }
            _release.Wait(60000);   // hold: nothing more is sent until the test ends
        }
        catch { }
        finally { try { ctx.Response.Abort(); } catch { } }
    }

    public void Dispose() { _release.Set(); try { _listener.Close(); } catch { } _release.Dispose(); }
}
