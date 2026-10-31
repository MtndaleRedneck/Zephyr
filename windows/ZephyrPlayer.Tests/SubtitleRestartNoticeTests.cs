using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>CP2 round 4 (Instinct, approved fallback): changing the subtitle size
    /// during playback saves it and shows a restart popup. It never touches the playing
    /// movie, the MediaPlayer or the LibVLC; the saved size is used when the app next
    /// creates its LibVLC (SubtitleSizePathTests.SavedSize_AppliesAtTheNextLibVlcCreation).</summary>
    public class SubtitleRestartNoticeTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
        private static string ClipUrl => new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4")).AbsoluteUri;
        private static void Note(string name, string text) { try { File.WriteAllText(Path.Combine(OutDir, "cp2-unit-" + name + ".txt"), text); } catch { } }

        private static void OnSta(Action body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(90000), "STA body did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void PumpUntil(Func<bool> test, string message, int ms = 8000)
        {
            var until = Environment.TickCount64 + ms;
            while (!test() && Environment.TickCount64 < until)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
            Assert.True(test(), message);
        }
        private static void Pump(int ms) { var t = Environment.TickCount64 + ms; PumpUntil(() => Environment.TickCount64 >= t, "pump"); }
        private static void Click(PlayerView v, string name) { var b = (Button)v.FindName(name)!; b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, b)); }

        [Fact]
        public void SizeChange_DuringPlayback_ShowsRestartPopup_LeavesPlaybackAlone_AndSaves()
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), "zephyr-r4-" + Guid.NewGuid().ToString("N") + ".json");
            var log = new System.Text.StringBuilder();
            try
            {
                OnSta(() =>
                {
                    Core.Initialize();
                    using var vlc = new LibVLC("--no-video", "--aout=dummy");
                    using var player = new MediaPlayer(vlc);
                    var settings = new PlayerSettings(settingsPath);
                    var view = new PlayerView(vlc, player, settings);
                    var host = new Window { Content = view, Width = 1024, Height = 640, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                    host.Show();
                    try
                    {
                        var shown = new System.Collections.Generic.List<(string Title, string Text)>();
                        view.RestartNoticeOverride = (title, text) => shown.Add((title, text));
                        Assert.Equal("M", view.LibVlcSubtitleSize); // the size the running LibVLC carries

                        var start = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                        PumpUntil(() => start.IsCompleted && player.State == VLCState.Playing && player.Time > 300, "clip did not play");
                        var media = player.Media;
                        var mediaHandle = media!.NativeReference;
                        var playerHandle = player.NativeReference;
                        var generation = view.PlayGeneration;
                        var starts = view.StartCount;
                        var seekFraction = view.LastSeekFraction;
                        var timeBefore = player.Time;
                        log.AppendLine($"before: state {player.State}, time {timeBefore} ms, native media {mediaHandle}, native player {playerHandle}, generation {generation}, starts {starts}, saved size {settings.SubtitleSize}, LibVLC size {view.LibVlcSubtitleSize}");

                        Click(view, "CaptionsButton");
                        Click(view, "SubSizeL");
                        Pump(400);

                        // One popup, the exact approved words.
                        Assert.Single(shown);
                        Assert.Equal(PlayerBarLogic.SubtitleRestartTitle, shown[0].Title);
                        Assert.Equal(PlayerBarLogic.SubtitleRestartNotice("L"), shown[0].Text);
                        Assert.Contains("only after a full restart", shown[0].Text);
                        Assert.Contains("choose Quit Zephyr Player", shown[0].Text);
                        Assert.Contains("Closing the window is not enough", shown[0].Text);
                        Assert.Contains("will not restart or reopen it", shown[0].Text);
                        Assert.DoesNotContain("applied", shown[0].Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Equal(1, view.SubtitleRestartNoticeCount);

                        // Nothing that plays was touched: same media object and native handle,
                        // same MediaPlayer, no new start, no seek, still playing and moving on.
                        // (LibVLCSharp hands out a new wrapper per Media read; the native
                        // libvlc_media_t is the identity.)
                        Assert.Equal(mediaHandle, player.Media!.NativeReference);
                        Assert.Equal(playerHandle, player.NativeReference);
                        Assert.Equal(generation, view.PlayGeneration);
                        Assert.Equal(starts, view.StartCount);
                        Assert.Equal(seekFraction, view.LastSeekFraction);
                        PumpUntil(() => player.Time > timeBefore + 300, "playback did not keep moving after the size change");
                        Assert.Equal(VLCState.Playing, player.State);
                        Assert.Equal("M", view.LibVlcSubtitleSize); // the LibVLC still carries M
                        log.AppendLine($"after L: popups {shown.Count}, state {player.State}, time {player.Time} ms, same native media {player.Media!.NativeReference == mediaHandle}, generation {view.PlayGeneration}, starts {view.StartCount}, seek fraction {view.LastSeekFraction}");

                        // Saved: the file on disk carries L.
                        Assert.Equal("L", settings.SubtitleSize);
                        Assert.Equal("L", new PlayerSettings(settingsPath).SubtitleSize);
                        Assert.Contains("\"SubtitleSize\":\"L\"", File.ReadAllText(settingsPath).Replace(" ", ""));

                        // Same choice again: no second popup, a short reminder only.
                        Click(view, "CaptionsButton");
                        Click(view, "SubSizeL");
                        Pump(200);
                        Assert.Single(shown);
                        Assert.Contains("is saved", ((TextBlock)view.FindName("OsdText")).Text);
                        // Back to the size in use: nothing to restart for, no popup.
                        Click(view, "CaptionsButton");
                        Click(view, "SubSizeM");
                        Pump(200);
                        Assert.Single(shown);
                        Assert.Equal("Subtitle size: Medium (the size in use now)", ((TextBlock)view.FindName("OsdText")).Text);
                        Assert.Equal("M", new PlayerSettings(settingsPath).SubtitleSize);
                        // And S: a popup again, naming Small.
                        Click(view, "CaptionsButton");
                        Click(view, "SubSizeS");
                        Pump(200);
                        Assert.Equal(2, shown.Count);
                        Assert.Equal(PlayerBarLogic.SubtitleRestartNotice("S"), shown[1].Text);
                        Assert.Equal(mediaHandle, player.Media!.NativeReference);
                        Assert.Equal(starts, view.StartCount);
                        Assert.Equal(VLCState.Playing, player.State);
                        log.AppendLine($"L again: no popup; M (in use): no popup; S: popup {shown.Count}; still same media, starts {view.StartCount}, state {player.State}");
                        log.AppendLine("popup title: " + shown[0].Title);
                        log.AppendLine("popup text (L):");
                        log.AppendLine(shown[0].Text);
                    }
                    finally { view.StopPlaybackSource(); host.Close(); }
                });
            }
            finally
            {
                Note("r4-subtitle-restart-popup", log.ToString());
                try { File.Delete(settingsPath); } catch { }
            }
        }

        [Fact]
        public void RestartPopup_Window_CarriesTheWords_AndOneOk()
        {
            OnSta(() =>
            {
                var text = PlayerBarLogic.SubtitleRestartNotice("L");
                var dlg = FirewallSetup.NoteWindow(text, PlayerBarLogic.SubtitleRestartTitle);
                Assert.Equal("Subtitle size saved", dlg.Title);
                // The themed DarkDialog wraps its content in a 1 px top-line Border (CP4 item 84).
                var frame = Assert.IsType<Border>(dlg.Content);
                var stack = Assert.IsType<StackPanel>(frame.Child);
                Assert.Equal(text, ((TextBlock)stack.Children[0]).Text);
                var buttons = (StackPanel)stack.Children[1];
                Assert.Single(buttons.Children);
                Assert.Equal("OK", ((Button)buttons.Children[0]).Content);
                // Exactly one button anywhere in the dialog, and it is the OK.
                var all = new System.Collections.Generic.List<Button>();
                void Walk(object node)
                {
                    if (node is Button b) all.Add(b);
                    if (node is DependencyObject d)
                        foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
                }
                Walk(dlg);
                Assert.Single(all);
                Assert.Equal("OK", all[0].Content);
                dlg.Close();
            });
        }

        [Fact]
        public void RestartNotice_Words()
        {
            Assert.Equal(
                "Subtitle size is saved as Large.\n\n"
                + "The new size takes effect only after a full restart of Zephyr Player. The subtitles on screen stay the size they are now.\n\n"
                + "To restart: right-click the Zephyr Player icon in the system tray (next to the clock), choose Quit Zephyr Player, then open Zephyr Player again.\n\n"
                + "Closing the window is not enough, because Zephyr Player keeps running in the tray.\n\n"
                + "Your movie keeps playing. Zephyr will not restart or reopen it.",
                PlayerBarLogic.SubtitleRestartNotice("L"));
            Assert.StartsWith("Subtitle size is saved as Small.", PlayerBarLogic.SubtitleRestartNotice("S"));
            Assert.StartsWith("Subtitle size is saved as Medium.", PlayerBarLogic.SubtitleRestartNotice("M"));
        }
    }
}
