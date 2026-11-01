using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer
{
    /// <summary>
    /// 2.0.1 (Ezzy, Nov 1: "Not showing any icons"): our own movie frames for the Home
    /// grid when Windows has no thumbnail for a file. Windows often has none for MKV
    /// (HEVC without the paid extension, thumbnails off, no Explorer handler), so 2.0.0
    /// drew a blank tile for every one of his movies. The same libVLC that plays the
    /// files decodes ONE frame about 10 % in, off screen (memory video output, no
    /// window, no audio), through the same controlled byte stream as playback
    /// (SafeMediaSource). Frames are cached as small JPEGs under
    /// %LOCALAPPDATA%\Zephyr\thumbs keyed by path + size + modified time, so each file
    /// is decoded once. One grab at a time, each bounded; any failure = no picture
    /// (the drawn placeholder stays), never an exception.
    /// </summary>
    internal static class VlcThumbs
    {
        private static readonly object _gate = new();
        private static LibVLC? _vlc;
        private static readonly ConcurrentDictionary<string, byte> _failedThisSession = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Overridable for tests.</summary>
        internal static string CacheDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zephyr", "thumbs");

        /// <summary>Stable cache name for one version of one file. Pure, for tests.</summary>
        internal static string CacheKey(string path, long length, DateTime lastWriteUtc)
        {
            var text = Path.GetFullPath(path).ToUpperInvariant() + "|" + length.ToString(CultureInfo.InvariantCulture)
                + "|" + lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        /// <summary>Cached or freshly grabbed frame, width <paramref name="width"/> px,
        /// frozen. Null when the file cannot be decoded. Call off the UI thread.</summary>
        public static BitmapSource? Get(string path, int width = 320)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                var key = CacheKey(path, fi.Length, fi.LastWriteTimeUtc);
                var cached = Path.Combine(CacheDir, key + ".jpg");
                if (File.Exists(cached))
                {
                    var hit = LoadJpeg(cached);
                    if (hit != null) return hit;
                    try { File.Delete(cached); } catch { }
                }
                if (_failedThisSession.ContainsKey(key)) return null;
                var frame = Grab(path, width, TimeSpan.FromSeconds(10));
                if (frame == null) { _failedThisSession[key] = 1; return null; }
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    var tmp = cached + "." + Environment.ProcessId + ".tmp";
                    var enc = new JpegBitmapEncoder { QualityLevel = 88 };
                    enc.Frames.Add(BitmapFrame.Create(frame));
                    using (var fs = File.Create(tmp)) enc.Save(fs);
                    File.Move(tmp, cached, overwrite: true);
                }
                catch { } // a read-only profile still gets the in-memory frame
                return frame;
            }
            catch { return null; }
        }

        private static BitmapSource? LoadJpeg(string file)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>Decode one frame. Tries 10 %, then 25 % and 40 % when the frame
        /// is (nearly) black, which is common at fades and title cards.</summary>
        private static readonly byte[] Rv32 = { (byte)'R', (byte)'V', (byte)'3', (byte)'2' };

        internal static BitmapSource? Grab(string path, int width, TimeSpan timeout)
        {
            lock (_gate)
            {
                try
                {
                    Core.Initialize();
                    _vlc ??= new LibVLC("--no-audio", "--no-osd", "--no-spu", "--no-stats", "--no-video-title-show",
                        "--avcodec-hw=none", "--quiet");
                    using var src = new SafeMediaSource(_vlc, new Uri(path).AbsoluteUri);
                    var media = src.Media;
                    try { media.Parse(MediaParseOptions.ParseLocal, 3000).GetAwaiter().GetResult(); } catch { }
                    finally { try { media.ParseStop(); } catch { } }
                    var duration = media.Duration;
                    if (duration > 0)
                        media.AddOption(":start-time=" + (duration * 0.10 / 1000.0).ToString("0.0", CultureInfo.InvariantCulture));
                    media.AddOption(":no-audio");

                    // The frame size comes from the decoder through the format callback: libVLC 3
                    // skips the preparser for media fed through our stream input, so the tracks
                    // (and the movie's shape) are unknown until it decodes. A 2.39:1 film then
                    // gets a 2.39:1 thumbnail instead of being squeezed into 16:9.
                    var target = Math.Max(64, width & ~1);
                    var w = target;
                    var h = Math.Max(36, (int)Math.Round(target / (16.0 / 9.0)) & ~1);
                    var pitch = w * 4;
                    var buffer = IntPtr.Zero;
                    var buffers = new System.Collections.Generic.List<IntPtr>();
                    byte[]? copy = null;
                    var shown = new ManualResetEventSlim(false);
                    var want = 1;
                    var count = 0;
                    try
                    {
                        using var mp = new MediaPlayer(media) { EnableHardwareDecoding = false };
                        mp.SetVideoFormatCallbacks(
                            (ref IntPtr opaque, IntPtr chroma, ref uint fw, ref uint fh, ref uint pitches, ref uint lines) =>
                            {
                                var asp = fw > 0 && fh > 0 ? fw / (double)fh : 16.0 / 9.0;
                                if (asp < 0.5 || asp > 3.5) asp = 16.0 / 9.0;
                                w = target;
                                h = Math.Max(36, (int)Math.Round(target / asp) & ~1);
                                pitch = w * 4;
                                Marshal.Copy(Rv32, 0, chroma, 4);
                                fw = (uint)w; fh = (uint)h; pitches = (uint)pitch; lines = (uint)h;
                                buffer = Marshal.AllocHGlobal(pitch * h);
                                lock (buffers) buffers.Add(buffer);
                                return 1;
                            },
                            null);
                        mp.SetVideoCallbacks(
                            (opaque, planes) => { Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; },
                            null,
                            (opaque, picture) =>
                            {
                                if (Interlocked.Increment(ref count) < Volatile.Read(ref want) || shown.IsSet) return;
                                var c = new byte[pitch * h];
                                Marshal.Copy(buffer, c, 0, c.Length);
                                Volatile.Write(ref copy, c);
                                shown.Set();
                            });
                        if (!mp.Play()) return null;
                        var deadline = DateTime.UtcNow + timeout;
                        // Stream-backed media often has no duration before playback, so the
                        // start-time cannot apply and the first frame is the movie's black
                        // opening: then look at 10 % first. Seeking by position works once
                        // playing, whatever the duration says.
                        var tries = duration > 0 ? new[] { 0.25f, 0.40f } : new[] { 0.10f, 0.25f, 0.40f };
                        var attempt = 0;
                        byte[]? best = null; double bestLuma = -1;
                        while (true)
                        {
                            var left = deadline - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero || !shown.Wait(left)) break;
                            var frame = Volatile.Read(ref copy)!;
                            var luma = MeanLuma(frame);
                            if (luma > bestLuma) { best = frame; bestLuma = luma; }
                            if (luma >= 18 || attempt >= tries.Length) break;
                            // Dark frame: look a little further in.
                            Volatile.Write(ref copy, null);
                            Volatile.Write(ref want, Volatile.Read(ref count) + 2); // skip the frame shown at the seek
                            shown.Reset();
                            mp.Position = tries[attempt++];
                        }
                        try { mp.Stop(); } catch { }
                        if (best == null) return null;
                        for (var i = 3; i < best.Length; i += 4) best[i] = 255; // RV32 leaves X undefined
                        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, best, pitch);
                        bmp.Freeze();
                        return bmp;
                    }
                    finally
                    {
                        lock (buffers) foreach (var b in buffers) Marshal.FreeHGlobal(b);
                        shown.Dispose();
                    }
                }
                catch { return null; }
            }
        }

        /// <summary>Average brightness 0..255 of a BGRA buffer (sampled). Pure, for tests.</summary>
        internal static double MeanLuma(byte[] bgra)
        {
            if (bgra.Length < 4) return 0;
            long sum = 0; long n = 0;
            var step = Math.Max(4, (bgra.Length / 4 / 4096) * 4);
            for (var i = 0; i + 2 < bgra.Length; i += step) { sum += (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8; n++; }
            return n == 0 ? 0 : sum / (double)n;
        }

        /// <summary>A poster that ships with the folder wins over any video frame
        /// (Plex/Kodi/Jellyfin names). Pure lookup, for tests.</summary>
        internal static string? FolderArt(string dir)
        {
            foreach (var name in new[] { "poster", "folder", "cover", "movie", "fanart" })
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp" })
                {
                    var p = Path.Combine(dir, name + ext);
                    try { if (File.Exists(p)) return p; } catch { }
                }
            return null;
        }

        /// <summary>Decode a folder's own artwork at tile size (never full size).</summary>
        internal static BitmapSource? LoadArt(string file, int width)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = width;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
