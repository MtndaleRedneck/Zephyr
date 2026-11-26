using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>Carried from 2.0.2 Cp1Shell: a local sidecar subtitle is selected from the start and blended into the picture (engine path, headless).</summary>
    public class LocalSidecarCarriedTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

        private static int[] RenderFrameWhites(string movie, bool withSidecar, string outPng)
        {
            const int W = 640, H = 480;
            Core.Initialize();
            using var libvlc = new LibVLC("--no-audio", "--quiet");
            using var player = new MediaPlayer(libvlc);
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(W * H * 4);
            var frames = 0;
            MediaPlayer.LibVLCVideoLockCb lockCb = (IntPtr opaque, IntPtr planes) => { System.Runtime.InteropServices.Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; };
            MediaPlayer.LibVLCVideoDisplayCb displayCb = (IntPtr opaque, IntPtr picture) => { Interlocked.Increment(ref frames); };
            try
            {
                player.SetVideoFormat("RV32", W, H, W * 4);
                player.SetVideoCallbacks(lockCb, null, displayCb);
                var url = new Uri(movie).AbsoluteUri;
                using var safe = new SafeMediaSource(libvlc, url);
                // The product path: PlayerView attaches a local movie's sidecars exactly so.
                if (withSidecar) safe.AddSubtitles(SafeMediaSource.LocalSidecars(url));
                Assert.True(player.Play(safe.Media));
                var deadline = Environment.TickCount64 + 10000;
                while (player.State != VLCState.Playing && Environment.TickCount64 < deadline) Thread.Sleep(30);
                Thread.Sleep(1800);
                var spu = player.Spu; // no manual selection: the sidecar must be on from the start
                player.SetPause(true);
                Thread.Sleep(400);
                var px = new byte[W * H * 4];
                System.Runtime.InteropServices.Marshal.Copy(buffer, px, 0, px.Length);
                var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgr32, null, px, W * 4);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(OutDir, outPng))) enc.Save(fs);
                int whites = 0, blacks = 0;
                for (var y = H * 3 / 4; y < H; y++)
                    for (var x = 0; x < W * 7 / 8; x++)
                    {
                        var i = (y * W + x) * 4;
                        if (px[i] > 235 && px[i + 1] > 235 && px[i + 2] > 235) whites++;
                        if (px[i] < 20 && px[i + 1] < 20 && px[i + 2] < 20) blacks++;
                    }
                player.Stop();
                return new[] { whites, blacks, frames, spu };
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); GC.KeepAlive(lockCb); GC.KeepAlive(displayCb); }
        }

        [Fact]
        public void LocalSidecar_TextIsBlendedIntoThePicture()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1-subpx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var movie = Path.Combine(dir, "Clip.mp4");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4"), movie);
            var plain = RenderFrameWhites(movie, false, "cp1-subtitle-frame-off.png");
            File.WriteAllText(Path.Combine(dir, "Clip.srt"), "1\r\n00:00:00,000 --> 00:59:00,000\r\nZEPHYR SUBTITLE PROOF\r\n");
            var subs = RenderFrameWhites(movie, true, "cp1-subtitle-frame-on.png");
            var line = $"CP1_SUBPX off: whites={plain[0]} blacks={plain[1]} frames={plain[2]} | on: whites={subs[0]} blacks={subs[1]} frames={subs[2]} spu={subs[3]}";
            File.WriteAllText(Path.Combine(OutDir, "cp1-subtitle-frame.txt"), line);
            Assert.True(plain[2] > 0 && subs[2] > 0, "no frames rendered: " + line);
            Assert.True(subs[3] >= 0, "the local sidecar was not selected from the start: " + line);
            Assert.True(subs[0] - plain[0] > 150, "subtitle text not blended into the picture: " + line);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
