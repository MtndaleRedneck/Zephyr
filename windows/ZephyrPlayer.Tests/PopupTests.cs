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
}
