using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using LibVLCSharp.Shared;

namespace ZephyrPlayer;

/// <summary>Only controlled byte streams enter the native decoder. Native URL
/// access and non-strict playlist probing are never needed for these movies.</summary>
internal sealed class SafeMediaSource : IDisposable
{
    private readonly Stream _stream;
    private readonly SafeCallbackInput _input;
    private readonly System.Collections.Generic.List<string> _subtitles = new();
    internal Media Media { get; }
    private int _disposed;
    internal SafeMediaSource(LibVLC vlc, string source, CancellationToken cancellation = default)
    {
        _stream = Open(source,cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var header = new byte[4096]; var count = 0;
            while (count < header.Length) { var n = _stream.Read(header, count, header.Length-count); if (n == 0) break; count += n; }
            _stream.Position = 0;
            var demux = DetectDemux(header.AsSpan(0,count)) ?? throw new InvalidDataException("Unsupported movie content. Playlists cannot be opened as movies.");
            if (new Uri(source).IsFile && !ExtensionMatches(Path.GetExtension(new Uri(source).LocalPath),demux)) throw new InvalidDataException("Movie content does not match its format");
            _input = new SafeCallbackInput(_stream);
            Media = new Media(vlc, _input, ":demux=" + demux, ":no-sub-autodetect-file", ":sub-autodetect-path=", ":no-mkv-use-ordered-chapters", ":no-mkv-preload-local-dir");
        }
        catch { _input?.Dispose(); _stream.Dispose(); throw; }
    }
    internal static string? DetectDemux(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[0]==0x1a && b[1]==0x45 && b[2]==0xdf && b[3]==0xa3) return "mkv";
        if (b.Length >= 12 && (b.Slice(4,4).SequenceEqual("ftyp"u8) || b.Slice(4,4).SequenceEqual("moov"u8) || b.Slice(4,4).SequenceEqual("mdat"u8) || b.Slice(4,4).SequenceEqual("free"u8) || b.Slice(4,4).SequenceEqual("wide"u8))) return "mp4";
        if (b.Length >= 12 && b.Slice(0,4).SequenceEqual("RIFF"u8) && b.Slice(8,4).SequenceEqual("AVI "u8)) return "avi";
        if (b.Length >= 16 && b.Slice(0,16).SequenceEqual(new byte[]{0x30,0x26,0xb2,0x75,0x8e,0x66,0xcf,0x11,0xa6,0xd9,0,0xaa,0,0x62,0xce,0x6c})) return "asf";
        if (b.Length > 376 && b[0]==0x47 && b[188]==0x47 && b[376]==0x47) return "ts";
        if (b.Length > 388 && b[4]==0x47 && b[196]==0x47 && b[388]==0x47) return "ts";
        if (b.Length >= 4 && b[0]==0 && b[1]==0 && b[2]==1) { if (b[3]==0xba) return "ps"; if (b[3]==0xb3) return "mpgv"; }
        if (b.Length >= 9 && b.Slice(0,4).SequenceEqual(new byte[]{0x46,0x4c,0x56,1})) return "avformat";
        return null;
    }
    internal static bool ExtensionMatches(string extension,string demux) => extension.ToLowerInvariant() switch
    {
        ".mkv" or ".webm" => demux=="mkv", ".mp4" or ".m4v" or ".mov" => demux=="mp4",
        ".avi" => demux=="avi", ".wmv" => demux=="asf", ".ts" or ".m2ts" => demux=="ts",
        ".mpg" or ".mpeg" => demux is "ps" or "mpgv", ".flv" => demux=="avformat", _ => false
    };
    private static Stream Open(string source,CancellationToken cancellation = default)
    {
        var uri = new Uri(source);
        if (uri.IsFile) return new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (uri.Scheme != "http") throw new InvalidDataException("Unsupported media source");
        return new PeerRangeStream(uri,cancellation);
    }
    internal void AddSubtitle(string source,CancellationToken cancellation = default)
    {
        var path=StageSubtitle(source,cancellation);
        Media.AddSlave(MediaSlaveType.Subtitle,0,new Uri(path).AbsoluteUri);
    }
    /// <summary>Validate a subtitle source (phone sidecar, local sidecar next to the
    /// movie, or a file the user picked) and write a strict, bounded private copy.
    /// Returns the private copy's path; it is deleted with this source.</summary>
    internal string StageSubtitle(string source,CancellationToken cancellation = default)
    {
        // Phone sidecars carry no filename extension. Detect a supported text
        // format from contents and write a strict, bounded private local file.
        cancellation.ThrowIfCancellationRequested();
        using var stream = Open(source,cancellation);
        if (stream.Length > 2*1024*1024) throw new InvalidDataException("Subtitle too large");
        var raw = new MemoryStream();
        stream.CopyTo(raw);
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(raw.ToArray()), new UTF8Encoding(false,true), true);
            text = reader.ReadToEnd().TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException) when (new Uri(source).IsFile)
        {
            // Local .srt files are often saved in a Windows code page, not UTF-8:
            // read them as Latin-1 rather than refusing the user's own file.
            text = Encoding.Latin1.GetString(raw.ToArray());
        }
        cancellation.ThrowIfCancellationRequested();
        text=text.TrimStart();
        var ext = SubtitleExtension(text);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZephyrPlayer","Subtitles",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); var path=Path.Combine(dir,"subtitle"+ext);
        File.WriteAllText(path,text,new UTF8Encoding(false)); lock(_subtitles) _subtitles.Add(path);
        return path;
    }
    /// <summary>Local sidecars for a local movie (P0-3): "&lt;movie&gt;.srt/.ass/.ssa/.vtt"
    /// first, then "&lt;movie&gt;.*.srt" style language variants, from the movie's own
    /// folder, as file URIs. Remote sources have none. Bounded and never throws.</summary>
    internal static string[] LocalSidecars(string movieUrl)
    {
        try
        {
            var uri = new Uri(movieUrl);
            if (!uri.IsFile) return Array.Empty<string>();
            var movie = uri.LocalPath;
            var dir = Path.GetDirectoryName(movie);
            if (dir == null || !Directory.Exists(dir)) return Array.Empty<string>();
            var stem = Path.GetFileNameWithoutExtension(movie);
            var exts = new[] { ".srt", ".ass", ".ssa", ".vtt" };
            var exact = new System.Collections.Generic.List<string>();
            var variants = new System.Collections.Generic.List<string>();
            foreach (var file in Directory.EnumerateFiles(dir).Take(4000))
            {
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(name);
                if (Array.IndexOf(exts, ext.ToLowerInvariant()) < 0) continue;
                var baseName = Path.GetFileNameWithoutExtension(name);
                if (baseName.Equals(stem, StringComparison.OrdinalIgnoreCase)) exact.Add(file);
                else if (baseName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) variants.Add(file);
            }
            exact.Sort((a, b) => Array.IndexOf(exts, Path.GetExtension(a).ToLowerInvariant()).CompareTo(Array.IndexOf(exts, Path.GetExtension(b).ToLowerInvariant())));
            variants.Sort(StringComparer.OrdinalIgnoreCase);
            return exact.Concat(variants).Take(8).Select(f => new Uri(f).AbsoluteUri).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
    internal static string SubtitleExtension(string text)
    {
        if (text.IndexOf('\0') >= 0 || Matches(text, @"(?im)^[ \t]*(#EXTM3U|\[playlist\]|<\?xml|<asx|<html|<rss|<feed|#EXT-X-)")) throw new InvalidDataException("Unsupported subtitle content");
        // Reject suspicious external-resource override syntax as defense in depth.
        // This is not a guarantee about every native subtitle parser. Ordinary
        // URL text in subtitle dialogue/credits is harmless and retained.
        if (Matches(text,@"(?i)\{[^}]*\\(?:pict|img|clip)\s*\([^)]*(?:https?|file|smb|ftp)://")) throw new InvalidDataException("External subtitle resource refused");
        return text.TrimStart().StartsWith("WEBVTT",StringComparison.Ordinal) ? ".vtt"
            : Matches(text,@"(?im)^\[Script Info\][ \t]*\r?$") && Matches(text,@"(?im)^\[Events\][ \t]*\r?$") ? ".ass"
            : Matches(text,@"(?m)^[ \t]*\d{1,2}:\d{2}:\d{2}[,.]\d{3}[ \t]+-->[ \t]+\d{1,2}:\d{2}:\d{2}[,.]\d{3}") ? ".srt" : throw new InvalidDataException("Unsupported subtitle format");
    }
    internal void PreparationComplete() { if(_stream is PeerRangeStream peer) peer.PreparationToken=CancellationToken.None; }
    internal static void CleanOldSubtitles()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZephyrPlayer","Subtitles");
        try {foreach(var dir in Directory.GetDirectories(root)) if(Directory.GetLastWriteTimeUtc(dir)<DateTime.UtcNow.AddDays(-1)){try{Directory.Delete(dir,true);}catch{}}}catch{}
    }
    private static bool Matches(string text,string pattern)
    {
        // A pathological input (for example megabytes of blank lines) can run past
        // the match budget on a slow machine: that is a refusal, not a crash.
        try { return Regex.IsMatch(text,pattern,RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(250)); }
        catch (RegexMatchTimeoutException) { throw new InvalidDataException("Subtitle could not be checked in time"); }
    }
    internal void Abort() => _input.Abort();
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _input.Abort(); Media.ParseStop(); Media.Dispose(); _input.Dispose(); _stream.Dispose();
        string[] staged; lock(_subtitles) staged=_subtitles.ToArray();
        foreach(var path in staged) { try { Directory.Delete(Path.GetDirectoryName(path)!,true); } catch { } }
    }
}

/// <summary>Exceptions never cross the unmanaged callback. Closing does not
/// seek/reopen HTTP. Disposal aborts reads before draining callback ownership.</summary>
internal sealed class SafeCallbackInput : MediaInput
{
    private readonly Stream _stream;
    private readonly object _gate=new();
    private volatile bool _closing;
    private readonly byte[] _bytes=new byte[1024*1024];
    internal SafeCallbackInput(Stream stream){_stream=stream;CanSeek=stream.CanSeek;}
    public override bool Open(out ulong size)
    {
        size=ulong.MaxValue;
        try {lock(_gate){if(_closing)return false;size=(ulong)_stream.Length;_stream.Position=0;return true;}}catch{return false;}
    }
    public override int Read(IntPtr buffer,uint length)
    {
        try {lock(_gate){if(_closing)return -1;var n=_stream.Read(_bytes,0,(int)Math.Min(length,(uint)_bytes.Length));System.Runtime.InteropServices.Marshal.Copy(_bytes,0,buffer,n);return n;}}catch{return -1;}
    }
    public override bool Seek(ulong offset)
    {
        try {lock(_gate){if(_closing||offset>long.MaxValue)return false;_stream.Seek((long)offset,SeekOrigin.Begin);return true;}}catch{return false;}
    }
    public override void Close() { }
    internal void Abort() { _closing=true;if(_stream is PeerRangeStream peer)peer.Abort(); }
    protected override void Dispose(bool disposing)
    {
        Abort();
        lock(_gate){base.Dispose(disposing);}
    }
}

/// <summary>Seekable, exact-address range reads. No proxies, credentials,
/// cookies, redirects, or server-selected protocols reach libVLC.</summary>
internal sealed class PeerRangeStream : Stream
{
    private static readonly HttpClient Http = new(new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false,UseCookies=false,UseDefaultCredentials=false}) { Timeout=TimeSpan.FromSeconds(15) };
    private readonly Uri _uri;
    private readonly CancellationTokenSource _lifetime=new();
    private int _disposed;
    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _position;
    private long _length = -1;
    private readonly byte[] _buffer = new byte[256*1024];
    private int _count, _offset;
    internal CancellationToken PreparationToken;
    internal PeerRangeStream(Uri uri,CancellationToken cancellation = default) { _uri=uri; PreparationToken=cancellation; Connect(0); }
    private void Connect(long position)
    {
        _body?.Dispose(); _response?.Dispose(); _body=null; _response=null; _count=_offset=0;
        using var req=new HttpRequestMessage(HttpMethod.Get,_uri); req.Headers.Range=new RangeHeaderValue(position,null);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(PreparationToken,_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var response=Http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,timeout.Token).GetAwaiter().GetResult();
        try
        {
            var code=(int)response.StatusCode;
            if(code!=206 && !(code==200 && position==0)) throw new InvalidDataException("Movie server refused a direct byte range");
            var range=response.Content.Headers.ContentRange;
            var total=code==206 ? range?.Length : response.Content.Headers.ContentLength;
            if(code==206 && (range?.Unit!="bytes" || range.From!=position || range.To==null || range.To<position || range.To>=total)) throw new InvalidDataException("Incorrect movie byte range");
            if(total==null || total<=0 || total>16L*1024*1024*1024*1024 || (_length>=0 && total!=_length)) throw new InvalidDataException("Incorrect movie length");
            timeout.Token.ThrowIfCancellationRequested();
            var body=response.Content.ReadAsStream(timeout.Token);
            if(Volatile.Read(ref _disposed)!=0){body.Dispose();throw new ObjectDisposedException(nameof(PeerRangeStream));}
            _length=total.Value; _position=position; _response=response; _body=body;
        }
        catch { response.Dispose(); throw; }
    }
    public override int Read(byte[] buffer,int offset,int count)
    {
        if(Volatile.Read(ref _disposed)!=0)throw new ObjectDisposedException(nameof(PeerRangeStream));
        if(_position>=_length)return 0;
        if(_offset==_count)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(PreparationToken,_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            _count=_body!.ReadAsync(_buffer.AsMemory(),timeout.Token).AsTask().GetAwaiter().GetResult();_offset=0;
            if(_count==0)throw new EndOfStreamException("Movie ended before its declared length");
        }
        var n=(int)Math.Min(Math.Min(count,_count-_offset),_length-_position);
        Buffer.BlockCopy(_buffer,_offset,buffer,offset,n);_offset+=n;_position+=n;return n;
    }
    public override long Seek(long offset,SeekOrigin origin)
    {
        if(Volatile.Read(ref _disposed)!=0)throw new ObjectDisposedException(nameof(PeerRangeStream));
        var next=origin==SeekOrigin.Begin?offset:origin==SeekOrigin.Current?_position+offset:_length+offset;
        if(next<0||next>_length)throw new IOException("Invalid seek");
        if(next!=_position) {if(next==_length){_position=next;_body?.Dispose();_body=null;}else Connect(next);}return _position;
    }
    public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
    public override long Length=>_length;public override long Position{get=>_position;set=>Seek(value,SeekOrigin.Begin);}
    public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    internal void Abort()
    {
        if(Interlocked.Exchange(ref _disposed,1)==0){_lifetime.Cancel();}
        // Always close: a connect completing concurrently may have assigned a late body.
        _body?.Dispose();_response?.Dispose();
    }
    protected override void Dispose(bool disposing){if(disposing)Abort();base.Dispose(disposing);}
}
