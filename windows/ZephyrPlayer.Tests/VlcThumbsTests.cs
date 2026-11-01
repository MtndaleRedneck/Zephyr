using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>2.0.1 (Ezzy, Nov 1: "Not showing any icons"): Home tiles get a real movie
    /// frame from libVLC when Windows has no thumbnail - proven on REAL files (an H.264
    /// MKV and an HEVC MKV whose first second fades in from black), not fixture PNGs.</summary>
    public class VlcThumbsTests
    {
        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        private static double Luma(BitmapSource b)
        {
            var px = new byte[b.PixelWidth * b.PixelHeight * 4];
            b.CopyPixels(px, b.PixelWidth * 4, 0);
            return VlcThumbs.MeanLuma(px);
        }

        private static void Evidence(BitmapSource b, string name)
        {
            try
            {
                var dir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b));
                using var f = File.Create(Path.Combine(dir, "thumb-" + name + ".png")); enc.Save(f);
            }
            catch { }
        }

        [Theory]
        [InlineData("clip.mkv")]       // H.264 in MKV
        [InlineData("thumb-hevc.mkv")] // HEVC in MKV, black at the start
        public void Grab_RealMkv_GivesARealFrame(string name)
        {
            var sw = Stopwatch.StartNew();
            var b = VlcThumbs.Grab(Fixture(name), 320, TimeSpan.FromSeconds(15));
            sw.Stop();
            Assert.NotNull(b);
            Assert.Equal(320, b!.PixelWidth);
            Assert.InRange(b.PixelHeight, 150, 240);
            var luma = Luma(b);
            Assert.True(luma >= 18, $"{name}: frame is black (mean luma {luma:0.0})");
            Evidence(b, name);
            Console.WriteLine($"THUMB {name}: {b.PixelWidth}x{b.PixelHeight} luma {luma:0.0} in {sw.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void Get_CachesTheFrameOnDisk_SecondCallIsACacheHit()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-thumbs-" + Guid.NewGuid().ToString("N"));
            var lib = Path.Combine(dir, "lib"); Directory.CreateDirectory(lib);
            var movie = Path.Combine(lib, "The Sheep Detectives 2026.mkv");
            File.Copy(Fixture("thumb-hevc.mkv"), movie);
            var prev = VlcThumbs.CacheDir;
            VlcThumbs.CacheDir = Path.Combine(dir, "thumbs");
            try
            {
                var first = VlcThumbs.Get(movie);
                Assert.NotNull(first);
                var files = Directory.GetFiles(VlcThumbs.CacheDir, "*.jpg");
                Assert.Single(files);
                var sw = Stopwatch.StartNew();
                var second = VlcThumbs.Get(movie);
                sw.Stop();
                Assert.NotNull(second);
                Assert.True(sw.ElapsedMilliseconds < 1500, $"cache hit took {sw.ElapsedMilliseconds} ms");
                // A changed file is a new key (size/time), so a re-encoded movie gets a new frame.
                var fi = new FileInfo(movie);
                Assert.NotEqual(VlcThumbs.CacheKey(movie, fi.Length, fi.LastWriteTimeUtc), VlcThumbs.CacheKey(movie, fi.Length + 1, fi.LastWriteTimeUtc));
                Assert.Equal(VlcThumbs.CacheKey(movie, fi.Length, fi.LastWriteTimeUtc), VlcThumbs.CacheKey(movie.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc));
            }
            finally
            {
                VlcThumbs.CacheDir = prev;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void NotAMovie_GivesNoPicture_NeverThrows()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-thumbs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var fake = Path.Combine(dir, "broken.mkv");
                File.WriteAllText(fake, "not a movie");
                Assert.Null(VlcThumbs.Grab(fake, 320, TimeSpan.FromSeconds(5)));
                Assert.Null(VlcThumbs.Get(Path.Combine(dir, "missing.mkv")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void FolderArt_PosterBeatsFolderBeatsCover()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-art-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Null(VlcThumbs.FolderArt(dir));
                File.WriteAllBytes(Path.Combine(dir, "cover.png"), new byte[] { 1 });
                Assert.EndsWith("cover.png", VlcThumbs.FolderArt(dir));
                File.WriteAllBytes(Path.Combine(dir, "folder.jpg"), new byte[] { 1 });
                Assert.EndsWith("folder.jpg", VlcThumbs.FolderArt(dir));
                File.WriteAllBytes(Path.Combine(dir, "poster.jpg"), new byte[] { 1 });
                Assert.EndsWith("poster.jpg", VlcThumbs.FolderArt(dir));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
