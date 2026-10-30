using System;
using System.IO;
using System.Reflection;
using System.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Xunit;

namespace ZephyrPlayer.Tests;

// 2.0 volume model (owner decision 2): the player's OWN volume, 0-100 % on the
// slider, with the Settings boost (100-200 %, kept) as extra decoder gain, and
// the player's OWN mute. Windows' master volume and mute are never read or
// written by the player, so a Windows-side state (or a failed Windows read) can
// never move the player. The VolumeService helper mapping stays pinned below
// because the service still exists (it no longer drives any UI).
public class VolumeMuteTests
{
    [Fact]
    public void MuteFromRead_Success_MapsToBool()
    {
        Assert.Equal((bool?)true, VolumeService.MuteFromRead(0, 1));
        Assert.Equal((bool?)false, VolumeService.MuteFromRead(0, 0));
    }

    [Fact]
    public void MuteFromRead_FailureHResult_IsNull_NotUnmuted()
    {
        const int E_FAIL = unchecked((int)0x80004005);
        Assert.Null(VolumeService.MuteFromRead(E_FAIL, 0));
        Assert.Null(VolumeService.MuteFromRead(E_FAIL, 1));
    }

    [Fact]
    public void Gain_IsSliderTimesBoost_BoostKept()
    {
        Assert.Equal(62, PlayerView.GainFor(62, 100));
        Assert.Equal(93, PlayerView.GainFor(62, 150));
        Assert.Equal(200, PlayerView.GainFor(100, 200)); // Boost 200 % still doubles
        Assert.Equal(0, PlayerView.GainFor(0, 200));
        Assert.Equal(100, PlayerView.GainFor(150, 100)); // slider clamps at 100
    }

    internal static void Sta(Action<PlayerView, PlayerSettings> body, VolumeService? volume = null)
    {
        string? error = null;
        var path = Path.Combine(Path.GetTempPath(), "zephyr-volmute-" + Guid.NewGuid().ToString("N") + ".json");
        var t = new Thread(() =>
        {
            try
            {
                Core.Initialize();
                var libvlc = new LibVLC();
                var player = new MediaPlayer(libvlc);
                var settings = new PlayerSettings(path);
                var w = new PlayerView(libvlc, player, settings, volume);
                body(w, settings);
                w.ReleasePlayback();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception e) { error = e.ToString(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(30000);
        if (error != null)
        {
            var f = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "sta-error-" + Guid.NewGuid().ToString("N") + ".txt");
            try { File.WriteAllText(f, error); } catch { }
            Assert.True(false, "STA body error (full text also in " + f + "):\n" + error);
        }
        try { File.Delete(path); } catch { }
    }

    private static void InvokePoll(PlayerView w) =>
        typeof(PlayerView).GetMethod("UpdatePosition", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);

    [Fact]
    public void Mute_IsThePlayersOwn_WindowsStateNeverMovesIt()
    {
        foreach (var forced in new[] { VolumeService.ForcedMute.True, VolumeService.ForcedMute.False, VolumeService.ForcedMute.Unknown })
        {
            Sta((w, _) =>
            {
                w.TrackedPlayerMute = true;
                InvokePoll(w);
                Assert.True(w.TrackedPlayerMute);
                Assert.True(w.SharedMute);
                w.TrackedPlayerMute = false;
                InvokePoll(w);
                Assert.False(w.TrackedPlayerMute);
                Assert.False(w.SharedMute);
            }, new VolumeService { ForcedMuteOverride = forced });
        }
    }

    [Fact]
    public void Slider_And_Phone_Drive_OwnVolume_Label_ShowsPercent()
    {
        Sta((w, settings) =>
        {
            var label = (System.Windows.Controls.TextBlock)w.FindName("VolumeLabel")!;
            w.SetVolumeTied(62);
            Assert.Equal(62, w.SharedVolume);
            Assert.Equal("62%", label.Text);
            Assert.Equal(62, settings.LastVolume);
            w.SetVolumeTied(140); // phone values clamp to 0-100
            Assert.Equal(100, w.SharedVolume);
            w.SetMuteTied(true);
            Assert.Equal("Muted", label.Text);
            Assert.True(((System.Windows.Controls.Slider)w.FindName("VolumeSlider")!).Opacity < 1);
            w.SetMuteTied(false);
            Assert.Equal("100%", label.Text);
            var slider = (System.Windows.Controls.Slider)w.FindName("VolumeSlider")!;
            Assert.Null(slider.ToolTip); // "Volume (matches Windows)" is gone
        });
    }

    [Fact]
    public void UpDown_Step_Volume_By_Five_And_Clamp()
    {
        Sta((w, _) =>
        {
            w.SetVolumeTied(62);
            w.StepVolume(+PlayerView.VolumeStep);
            Assert.Equal(67, w.SharedVolume);
            w.StepVolume(-PlayerView.VolumeStep);
            w.StepVolume(-PlayerView.VolumeStep);
            Assert.Equal(57, w.SharedVolume);
            w.SetVolumeTied(98);
            w.StepVolume(+PlayerView.VolumeStep);
            Assert.Equal(100, w.SharedVolume);
            w.SetVolumeTied(3);
            w.StepVolume(-PlayerView.VolumeStep);
            Assert.Equal(0, w.SharedVolume);
        });
    }
}
