using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>Carried forward unchanged in meaning from 2.0.2 (Cp1RepairTests, Cp1ShellTests): non-UTF-8 local
    /// subtitles decode as Windows-1252 while phone subtitles stay strict, UTF-8/UTF-16 BOMs decode, local sidecars
    /// are found exact-name first then language variants and nothing else, and a sidecar registers a real track.</summary>
    public class SubtitleCarryForwardTests
    {
        private static byte[] Cp1252Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "cp1252.srt"));
        private const string Cp1252Line = "Price \u20AC5, \u201Cquoted\u201D caf\u00E9 \u2013 it\u2019s fine";
        private static void Note(string name, string text) { try { File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "carry-" + name + ".txt"), text); } catch { } }
        private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length); return r; }

        [Fact]
        public void LocalSubtitle_Windows1252_DecodesEuroAndCurlyQuotes()
        {
            var raw = Cp1252Fixture;
            Assert.Contains((byte)0x80, raw); // the byte Latin-1 turned into U+0080
            var text = SafeMediaSource.DecodeSubtitle(raw, localFile: true);
            Assert.Contains(Cp1252Line, text);
            Assert.DoesNotContain('\u0080', text);
            Assert.DoesNotContain('\u0093', text);
        }

        [Fact]
        public void LocalSubtitle_Windows1252_StagedFileIsCorrectUtf8()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1252-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var movie = Path.Combine(dir, "Movie.mp4");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4"), movie);
                var srt = Path.Combine(dir, "Movie.srt");
                File.WriteAllBytes(srt, Cp1252Fixture);
                Core.Initialize();
                using var vlc = new LibVLC("--no-video", "--aout=dummy");
                using var safe = new SafeMediaSource(vlc, new Uri(movie).AbsoluteUri);
                var staged = safe.StageSubtitle(new Uri(srt).AbsoluteUri);
                var written = File.ReadAllText(staged, new UTF8Encoding(false, true));
                Assert.Contains(Cp1252Line, written);
                Note("cp1252-subtitle", "staged: " + written.Replace("\r", "").Replace("\n", " | "));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void PhoneSubtitle_NonUtf8_StaysRefused()
        {
            Assert.Throws<DecoderFallbackException>(() => SafeMediaSource.DecodeSubtitle(Cp1252Fixture, localFile: false));
        }

        [Fact]
        public void Subtitle_Utf8AndUtf16Boms_Decode()
        {
            const string line = "1\r\n00:00:00,000 --> 00:00:02,000\r\n\u20AC \u201Cok\u201D\r\n";
            var utf8 = Encoding.UTF8.GetBytes(line);
            var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF }; utf8Bom = Concat(utf8Bom, utf8);
            var utf16le = Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(line));
            var utf16be = Concat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(line));
            foreach (var local in new[] { true, false })
            {
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf8, local));
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf8Bom, local));
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf16le, local));
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf16be, local));
            }
        }

        [Fact]
        public void LocalSidecars_ExactNameFirst_ThenLanguageVariants_NothingElse()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1-subs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var movie = Path.Combine(dir, "My Movie.mp4");
            File.WriteAllBytes(movie, new byte[] { 0 });
            foreach (var name in new[] { "My Movie.en.srt", "My Movie.vtt", "My Movie.srt", "Other.srt", "My Movie Extras.srt", "My Movie.txt" })
                File.WriteAllText(Path.Combine(dir, name), "1\n00:00:00,000 --> 00:00:01,000\nHi\n");
            var found = SafeMediaSource.LocalSidecars(new Uri(movie).AbsoluteUri).Select(u => Path.GetFileName(new Uri(u).LocalPath)).ToArray();
            Assert.Equal(new[] { "My Movie.srt", "My Movie.vtt", "My Movie.en.srt" }, found);
            Assert.Empty(SafeMediaSource.LocalSidecars("http://127.0.0.1:1/s/x/1"));
            try { Directory.Delete(dir, true); } catch { }
        }

        [Fact]
        public void LocalSidecar_RegistersRealTrack_DisableEntryFiltered_SelectWorks()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1-sidecar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var movie = Path.Combine(dir, "Clip.mp4");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4"), movie);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.srt"), Path.Combine(dir, "Clip.srt"));
            Core.Initialize();
            using var libvlc = new LibVLC("--no-video", "--aout=dummy");
            using var player = new MediaPlayer(libvlc);
            var url = new Uri(movie).AbsoluteUri;
            using var safe = new SafeMediaSource(libvlc, url);
            foreach (var sub in SafeMediaSource.LocalSidecars(url)) safe.AddSubtitle(sub);
            Assert.True(player.Play(safe.Media), "libVLC refused the local fixture");
            var deadline = Environment.TickCount64 + 10000;
            while (MainWindow.RealTracks(player.SpuDescription).Length == 0 && Environment.TickCount64 < deadline) Thread.Sleep(50);
            var real = MainWindow.RealTracks(player.SpuDescription);
            Assert.NotEmpty(real);
            Assert.DoesNotContain(real, t => t.Id == -1);
            Console.WriteLine("CP1_SUBS raw=[" + string.Join(", ", (player.SpuDescription ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>()).Select(t => t.Id + ":" + t.Name)) + "] spu=" + player.Spu);
            Assert.True(player.SetSpu(real[0].Id));
            deadline = Environment.TickCount64 + 5000;
            while (player.Spu != real[0].Id && Environment.TickCount64 < deadline) Thread.Sleep(50);
            Assert.Equal(real[0].Id, player.Spu);
            Assert.True(player.SetSpu(-1));
            deadline = Environment.TickCount64 + 5000;
            while (player.Spu != -1 && Environment.TickCount64 < deadline) Thread.Sleep(50);
            Assert.Equal(-1, player.Spu);
            player.Stop();
        }
    }
}
