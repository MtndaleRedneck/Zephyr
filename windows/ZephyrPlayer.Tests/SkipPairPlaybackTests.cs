using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;

namespace ZephyrPlayer.Tests
{
    // Native Windows test: actual WPF Button.Click routes against a real
    // decoded 8-minute WAV. No fake player, no direct handler invocation.
    public class SkipPairPlaybackTests
    {
        private static void Await(Func<bool> predicate, string reason, int timeoutMs = 8000)
        {
            var until = Environment.TickCount64 + timeoutMs;
            do
            {
                if (predicate()) return;
                // Let real WPF Changed/restyle/flash callbacks run on this STA.
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(30);
            } while (Environment.TickCount64 < until);
            Assert.True(predicate(), reason);
        }

        private static void WriteWav(string path)
        {
            const int rate = 8000, seconds = 480;
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + rate * seconds * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(rate * seconds * 2);
            for (var i = 0; i < rate * seconds; i++)
                w.Write((short)(Math.Sin(i * 220 * 2 * Math.PI / rate) * 1000));
        }

        [Fact]
        public void All_Four_Real_Buttons_Seek_The_Decoded_Media_By_Their_Own_Pair_Interval()
        {
            Exception? error = null;
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-real-skips-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var thread = new Thread(() =>
            {
                try
                {
                    var wav = Path.Combine(dir, "seek-proof.wav");
                    WriteWav(wav);
                    Core.Initialize();
                    using var vlc = new LibVLC("--no-video", "--aout=dummy");
                    using var player = new LibVLCSharp.Shared.MediaPlayer(vlc);
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                    var window = new PlayerView(vlc, player, settings, new VolumeService());
                    try
                    {
                        // Same engine consumed by PlayerView's real skip handlers.
                        using var media = new Media(vlc, wav, FromType.FromPath);
                        Assert.True(player.Play(media), "libVLC refused the real media fixture");
                        Await(() => player.State == VLCState.Playing && player.Length >= 479000,
                            "real media did not decode with the expected duration");
                        player.SetPause(true);
                        Await(() => player.State == VLCState.Paused, "real media did not pause");
                        foreach (var secondary in new[] { 30, 180 })
                        {
                            settings.SecondarySkipSeconds = secondary;
                            foreach (var step in new[] {
                                (Name: "SkipBackButton", Delta: -10),
                                (Name: "SkipForwardButton", Delta: 10),
                                (Name: "SecondarySkipBackButton", Delta: -secondary),
                                (Name: "SecondarySkipForwardButton", Delta: secondary) })
                            {
                                player.Time = 240000;
                                Await(() => Math.Abs(player.Time - 240000) <= 1000,
                                    "could not reset real media to midpoint");
                                var before = player.Time;
                                var button = (Button)window.FindName(step.Name)!;
                                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                                var expected = before + step.Delta * 1000L;
                                Await(() => Math.Abs(player.Time - expected) <= 1000,
                                    step.Name + " expected " + expected + "ms, got " + player.Time + "ms");
                                Assert.Equal(VLCState.Paused, player.State);
                                if(step.Name.StartsWith("Secondary",StringComparison.Ordinal)) {
                                    var field=typeof(PlayerView).GetField(step.Delta<0?"_secondaryBackHighlight":"_secondaryForwardHighlight",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                                    var highlight=(SecondarySkipHighlight)field.GetValue(window)!;
                                    Assert.True(highlight.HasAnimatedProperties,"secondary click did not start traveling highlight");
                                    var label=(TextBlock)window.FindName(step.Delta<0?"SecondarySkipBackLabel":"SecondarySkipForwardLabel")!;
                                    Assert.True(label.RenderTransform.Value.IsIdentity,"duration label moved with animation");
                                    player.Time=240000;Await(()=>Math.Abs(player.Time-240000)<=1000,"reset midpoint for flash-off proof");
                                    settings.ButtonFlash=false;var offBefore=player.Time;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
                                    Await(()=>Math.Abs(player.Time-(offBefore+step.Delta*1000L))<=1000,"flash off changed seek behavior");
                                    Assert.False(highlight.HasAnimatedProperties,"flash off must disable traveling highlight");settings.ButtonFlash=true;
                                }

                                Console.WriteLine($"REAL_SKIP_PASS {step.Name}: before={before}ms delta={step.Delta}s after={player.Time}ms");
                            }
                        }
                    }
                    finally { player.Stop(); window.ReleasePlayback(); }
                }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(100000), "Real skip playback test exceeded 100 seconds");
            try { Directory.Delete(dir, true); } catch { }
            if (error != null) throw new Exception("Real skip playback test failed", error);
        }
    }
}
