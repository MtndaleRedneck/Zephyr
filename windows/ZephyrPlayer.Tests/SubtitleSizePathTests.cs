using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>CP2 repair round (Instinct CP2 review 1, subtitle size): which libVLC 3
    /// paths change the subtitle text size, measured in decoded pictures. libVLC renders
    /// into memory (vmem callbacks, 1280x720 RV32) with the app's own subtitle options,
    /// and the probe measures the white text's height and its gap to the picture bottom.
    /// The instance-option sanity (S vs L at LibVLC creation) is asserted; the per-media
    /// and new-MediaPlayer results are RECORDED (cp2-unit-r2-subtitle-size-paths.txt),
    /// because they are the facts being established, not a behaviour Zephyr relies on.</summary>
    public class SubtitleSizePathTests
    {
        private const int W = 1280, H = 720;
        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

        private sealed class Vmem : IDisposable
        {
            private readonly IntPtr _buf = Marshal.AllocHGlobal(W * H * 4);
            private readonly MediaPlayer.LibVLCVideoLockCb _lock;
            private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
            private int _frames;
            public int Frames => Volatile.Read(ref _frames);
            public Vmem(MediaPlayer mp)
            {
                _lock = (opaque, planes) => { Marshal.WriteIntPtr(planes, _buf); return IntPtr.Zero; };
                _display = (opaque, picture) => Interlocked.Increment(ref _frames);
                mp.SetVideoCallbacks(_lock, null, _display);
                mp.SetVideoFormat("RV32", W, H, W * 4);
            }
            /// <summary>(text height, gap from the text's last row to the picture bottom), in
            /// picture pixels; (0, -1) when no white text is in the picture.</summary>
            public (int Height, int Gap) Measure()
            {
                var px = new byte[W * H * 4];
                Marshal.Copy(_buf, px, 0, px.Length);
                int top = -1, bottom = -1;
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var i = (y * W + x) * 4;
                        if (px[i] > 200 && px[i + 1] > 200 && px[i + 2] > 200) { if (top < 0) top = y; bottom = y; break; }
                    }
                return top < 0 ? (0, -1) : (bottom - top + 1, H - 1 - bottom);
            }
            public void Dispose() { GC.KeepAlive(_lock); GC.KeepAlive(_display); Marshal.FreeHGlobal(_buf); }
        }

        private static (int Height, int Gap) Play(LibVLC vlc, MediaPlayer mp, Vmem g, params string[] mediaOptions)
        {
            var media = new Media(vlc, new Uri(Fixture("sub-probe.mp4")));
            media.AddOption(":sub-file=" + Fixture("sub-probe.srt"));
            foreach (var o in mediaOptions) media.AddOption(o);
            var start = g.Frames;
            Assert.True(mp.Play(media), "libVLC refused the probe media");
            var until = Environment.TickCount64 + 15000;
            while ((mp.Time < 1500 || g.Frames < start + 5) && Environment.TickCount64 < until) Thread.Sleep(50);
            Thread.Sleep(300);
            var r = g.Measure();
            mp.Stop();
            media.Dispose();
            return r;
        }

        private static LibVLC Instance(string size) =>
            new LibVLC(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(PlayerBarLogic.SubtitleOptions(size, 1.0), new[] { "--no-audio" })));

        [Fact]
        public void SubtitleSize_WhichLibVlcPathsChangeIt()
        {
            Core.Initialize();
            var log = new System.Text.StringBuilder();
            using (var v0 = new LibVLC("--no-audio")) log.Append($"libVLC {v0.Version}; ");
            log.AppendLine($"picture {W}x{H}; app subtitle options per size (PlayerBarLogic.SubtitleOptions, dpi 1.0)");
            (int Height, int Gap) s, l, sameNext, sameNextRel, sameNextMargin, newPlayer, newPlayerRel;
            using (var vlc = Instance("S"))
            {
                using var mp = new MediaPlayer(vlc); using var g = new Vmem(mp);
                s = Play(vlc, mp, g);
                // The SAME MediaPlayer, the NEXT media, with per-media options.
                sameNext = Play(vlc, mp, g, ":sub-text-scale=78");
                sameNextRel = Play(vlc, mp, g, ":freetype-rel-fontsize=10");
                sameNextMargin = Play(vlc, mp, g, ":sub-margin=300");
                // A NEW MediaPlayer on the same LibVLC, its first media with the option.
                using (var mp2 = new MediaPlayer(vlc))
                using (var g2 = new Vmem(mp2))
                {
                    newPlayer = Play(vlc, mp2, g2, ":sub-text-scale=78");
                    newPlayerRel = Play(vlc, mp2, g2, ":freetype-rel-fontsize=10");
                }
            }
            using (var vlcL = Instance("L"))
            {
                using var mp = new MediaPlayer(vlcL); using var g = new Vmem(mp);
                l = Play(vlcL, mp, g);
            }
            static string Fx((int Height, int Gap) m) => m.Gap < 0 ? "no text found" : $"text {m.Height} px tall, {m.Gap} px above the picture bottom";
            static string Changed((int Height, int Gap) m, (int Height, int Gap) b) => m.Gap >= 0 && (Math.Abs(m.Height - b.Height) >= 3 || Math.Abs(m.Gap - b.Gap) >= 3) ? "CHANGED vs S" : "same as S";
            log.AppendLine($"S at LibVLC creation (--sub-text-scale=50): {Fx(s)}");
            log.AppendLine($"L at LibVLC creation (--sub-text-scale=78), new LibVLC + new MediaPlayer: {Fx(l)}  [{Changed(l, s)}]");
            log.AppendLine($"S LibVLC, same MediaPlayer, next media with :sub-text-scale=78: {Fx(sameNext)}  [{Changed(sameNext, s)}]");
            log.AppendLine($"S LibVLC, same MediaPlayer, next media with :freetype-rel-fontsize=10: {Fx(sameNextRel)}  [{Changed(sameNextRel, s)}]");
            log.AppendLine($"S LibVLC, same MediaPlayer, next media with :sub-margin=300: {Fx(sameNextMargin)}  [{Changed(sameNextMargin, s)}]");
            log.AppendLine($"S LibVLC, NEW MediaPlayer, first media with :sub-text-scale=78: {Fx(newPlayer)}  [{Changed(newPlayer, s)}]");
            log.AppendLine($"S LibVLC, NEW MediaPlayer, next media with :freetype-rel-fontsize=10: {Fx(newPlayerRel)}  [{Changed(newPlayerRel, s)}]");
            try { File.WriteAllText(Path.Combine(OutDir, "cp2-unit-r2-subtitle-size-paths.txt"), log.ToString()); } catch { }
            // The probe can see the size: S and L at creation differ.
            Assert.True(s.Gap >= 0, "no subtitle text found in the S picture\n" + log);
            Assert.True(l.Height >= s.Height + 4, "L at creation is not taller than S\n" + log);
        }
    }
}
