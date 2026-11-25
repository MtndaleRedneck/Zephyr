using System;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace ZephyrPlayer.Tests;

// The 2.0.2 bar pop-ups in the 3.0 shell: Subtitles, Playback speed, Sleep timer.
public class PopupTests
{
    private static void Click(MainWindow w, string name)
    {
        var b = (Button)w.FindName(name)!;
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, b));
    }

    [Fact]
    public void RestartNotice_Words_SayQuitPlayerFromTray_NotAnotherApp()
    {
        var text = PlayerBarLogic.SubtitleRestartNotice("L");
        Assert.Contains("Subtitle size is saved as Large.", text);
        Assert.Contains("Quit Zephyr Player", text);
        Assert.Contains("open Zephyr Player again", text);
        Assert.Contains("Your movie keeps playing", text);
        Assert.DoesNotContain("Android", text);
        Assert.Equal("Subtitle size saved", PlayerBarLogic.SubtitleRestartTitle);
        Assert.Contains("--sub-text-scale=78", PlayerBarLogic.SubtitleOptions("L", 1.0));
        Assert.Contains("--sub-text-scale=50", PlayerBarLogic.SubtitleOptions("S", 1.0));
    }

    [Fact]
    public void SpeedLogic_Snaps_And_Labels()
    {
        Assert.Equal("Normal", PlayerBarLogic.ChipLabel(1.0));
        Assert.Equal(2, PlayerBarLogic.ChipIndex(1.0));
        Assert.Equal(-1, PlayerBarLogic.ChipIndex(1.1));
        Assert.Equal(1.0, PlayerBarLogic.SnapRate(1.02), 3);
    }

    [Fact]
    public void Popups_OpenOne_AtATime_AndCloseOnEscape()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            w.Show();
            Click(w, "CaptionsButton");
            Assert.Equal("subtitles", w.OpenPopupName);
            Click(w, "SpeedButton");
            Assert.Equal("speed", w.OpenPopupName);
            Click(w, "SpeedButton");
            Assert.Null(w.OpenPopupName);
            Click(w, "SleepButton");
            Assert.Equal("sleep", w.OpenPopupName);
            w.ClosePopups();
            Assert.False(w.IsPopupOpen);
        });
    }

    [Fact]
    public void SubtitleSize_SavedAndNoticeShown_OnlyWhenItDiffersFromTheLibVlcSize()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            string? title = null; string? body = null;
            w.RestartNoticeOverride = (t, b) => { title = t; body = b; };
            w.LibVlcSubtitleSize = "M";
            w.ChooseSubtitleSize("L");
            Assert.Equal("L", settings.SubtitleSize);
            Assert.Equal(1, w.SubtitleRestartNoticeCount);
            Assert.Equal("Subtitle size saved", title);
            Assert.Equal(PlayerBarLogic.SubtitleRestartNotice("L"), body);
            w.ChooseSubtitleSize("L");                    // already saved: an on-screen line, no second dialog
            Assert.Equal(1, w.SubtitleRestartNoticeCount);
            w.ChooseSubtitleSize("M");                    // the size in use now
            Assert.Equal(1, w.SubtitleRestartNoticeCount);
            Assert.Equal("M", settings.SubtitleSize);
        });
    }

    [Fact]
    public void SubtitleDelay_StepsAndClamps()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            w.SetSubtitleDelay(w.SubtitleDelayMs + 100);
            Assert.Equal(100, w.SubtitleDelayMs);
            w.SetSubtitleDelay(1_000_000);
            Assert.Equal(60000, w.SubtitleDelayMs);
            w.SetSubtitleDelay(-1_000_000);
            Assert.Equal(-60000, w.SubtitleDelayMs);
            Assert.Equal("-60.0 s", PlayerBarLogic.FormatDelay(w.SubtitleDelayMs));
        });
    }

    [Fact]
    public void Speed_Choice_UpdatesRateBadgeAndHeader()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            w.SetRate(1.5);
            Assert.Equal(1.5, w.CurrentRate, 3);
            Assert.Equal("1.5x", ((TextBlock)w.FindName("SpeedLabel")!).Text.Replace("×", "x"));
            w.SetRate(1.0);
            Assert.Equal("", ((TextBlock)w.FindName("SpeedLabel")!).Text);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)w.FindName("SpeedLabel")!).Visibility);
        });
    }

    [Fact]
    public void Sleep_TimedChoice_PausesAndStays_ThenTurnsOff()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            w.Clock = () => now;
            w.SetSleep(15);
            Assert.True(w.Sleep.IsActive);
            Assert.Equal("15m", ((TextBlock)w.FindName("SleepLabel")!).Text);
            now = now.AddMinutes(16);
            w.SleepTick();
            Assert.Equal(1, w.SleepPausedCount);
            Assert.Equal("timer", w.LastSleepFire);
            Assert.False(w.Sleep.IsActive);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)w.FindName("SleepLabel")!).Visibility);
        });
    }

    [Fact]
    public void Sleep_EndOfMovie_IsDropped_WhenAnotherMovieStarts()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            w.SetSleep(SleepTimerState.EndOfMovie);
            Assert.True(w.Sleep.IsEndOfMovie);
            w.StopPlaybackSource();
            Assert.False(w.Sleep.IsActive);
        });
    }

    // ---- 2.0.2 SubtitleRestartNoticeTests, carried with their full assertions (the first two used to be name-only in the map) ----

    [Fact]
    public void RestartNotice_Words_AreTheExactApprovedText()
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
        var l = PlayerBarLogic.SubtitleRestartNotice("L");
        Assert.Contains("only after a full restart", l);
        Assert.Contains("choose Quit Zephyr Player", l);
        Assert.Contains("Closing the window is not enough", l);
        Assert.Contains("will not restart or reopen it", l);
        Assert.DoesNotContain("applied", l, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestartPopup_Window_CarriesTheWords_AndOneOk()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            var text = PlayerBarLogic.SubtitleRestartNotice("L");
            var dlg = FirewallSetup.NoteWindow(text, PlayerBarLogic.SubtitleRestartTitle);
            Assert.Equal("Subtitle size saved", dlg.Title);
            // The themed DarkDialog wraps its content in a 1 px top-line Border.
            var frame = Assert.IsType<Border>(dlg.Content);
            var stack = Assert.IsType<StackPanel>(frame.Child);
            Assert.Equal(text, ((TextBlock)stack.Children[0]).Text);
            var buttons = (StackPanel)stack.Children[1];
            Assert.Single(buttons.Children);
            Assert.Equal("OK", ((Button)buttons.Children[0]).Content);
            var all = new System.Collections.Generic.List<Button>();
            void Walk(object node)
            {
                if (node is Button b) all.Add(b);
                if (node is DependencyObject d)
                    foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(d)) Walk(child);
            }
            Walk(dlg);
            Assert.Single(all);
            Assert.Equal("OK", all[0].Content);
            dlg.Close();
        });
    }

    [Fact]
    public void SizeChange_DuringPlayback_ShowsRestartPopup_LeavesPlaybackAlone_AndSaves()
    {
        VolumeMuteTests.Sta((w, settings) =>
        {
            var shown = new System.Collections.Generic.List<(string Title, string Text)>();
            w.RestartNoticeOverride = (title, text) => shown.Add((title, text));
            Assert.Equal("M", w.LibVlcSubtitleSize);
            var clip = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4")).AbsoluteUri;
            var start = w.PlayFromUrlAsync(clip, null, Array.Empty<string>());
            var until = Environment.TickCount64 + 15000;
            while ((!start.IsCompleted || w.PlayerStateForTest != LibVLCSharp.Shared.VLCState.Playing) && Environment.TickCount64 < until)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                System.Threading.Thread.Sleep(10);
            }
            Assert.True(start.IsCompleted && w.PlayerStateForTest == LibVLCSharp.Shared.VLCState.Playing, "clip did not play");
            var media = w.CurrentMedia; var generation = w.PlayGeneration; var starts = w.StartCount; var seek = w.LastSeekFraction;
            Assert.NotNull(media);

            w.ChooseSubtitleSize("L");
            Assert.Single(shown);
            Assert.Equal(PlayerBarLogic.SubtitleRestartTitle, shown[0].Title);
            Assert.Equal(PlayerBarLogic.SubtitleRestartNotice("L"), shown[0].Text);
            Assert.Equal(1, w.SubtitleRestartNoticeCount);
            // Nothing that plays was touched: same media, no new start, no seek, still playing, LibVLC still carries M.
            Assert.Same(media, w.CurrentMedia);
            Assert.Equal(generation, w.PlayGeneration);
            Assert.Equal(starts, w.StartCount);
            Assert.Equal(seek, w.LastSeekFraction);
            Assert.Equal(LibVLCSharp.Shared.VLCState.Playing, w.PlayerStateForTest);
            Assert.Equal("M", w.LibVlcSubtitleSize);
            // Saved: the file on disk carries L.
            Assert.Equal("L", settings.SubtitleSize);
            Assert.Equal("L", new PlayerSettings(settings.PathForTest).SubtitleSize);
            Assert.Contains("\"SubtitleSize\":\"L\"", System.IO.File.ReadAllText(settings.PathForTest).Replace(" ", ""));
            // Same choice again: no second popup, a short reminder only.
            w.ChooseSubtitleSize("L");
            Assert.Single(shown);
            Assert.Contains("is saved", w.OsdMessage);
            // Back to the size in use: no popup.
            w.ChooseSubtitleSize("M");
            Assert.Single(shown);
            Assert.Equal("Subtitle size: Medium (the size in use now)", w.OsdMessage);
            Assert.Equal("M", new PlayerSettings(settings.PathForTest).SubtitleSize);
            // And S: a popup again, naming Small, still nothing touched.
            w.ChooseSubtitleSize("S");
            Assert.Equal(2, shown.Count);
            Assert.Equal(PlayerBarLogic.SubtitleRestartNotice("S"), shown[1].Text);
            Assert.Same(media, w.CurrentMedia);
            Assert.Equal(starts, w.StartCount);
            Assert.Equal(LibVLCSharp.Shared.VLCState.Playing, w.PlayerStateForTest);
            w.StopPlaybackSource();
        }, joinMs: 45000, mustFinish: true);
    }
}
