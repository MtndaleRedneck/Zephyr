using System;
using System.IO;
using System.Reflection;
using System.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Xunit;

namespace ZephyrPlayer.Tests;

// M2: a failed mute read must surface as UNKNOWN, never as "unmuted".
//
// Two levels, kept distinct on purpose:
//  * MuteFromRead/MuteAfterRead tests pin the tri-state HELPER mappings. On the
//    pre-fix tree they do not compile (the APIs did not exist) - that absence
//    is a compile failure, NOT a behavioral reproduction of the bug.
//  * The Sta() tests below reproduce the failure MODE behaviorally through the
//    real MainWindow call sites (poll via UpdatePosition, and SharedMute),
//    using the VolumeService override seam: a failed read must not move the
//    player's mute. Under the pre-fix code path (player.Mute = GetMute() with
//    GetMute() == false on failure) these scenarios would unmute the player.
//    They assert MainWindow's TRACKED player mute (TrackedPlayerMute), not
//    libvlc's Mute property: libvlc's read-back returns false whenever no
//    media is loaded or no audio device exists (found on the CI runner,
//    run 40964867086), which is exactly why the product tracks the applied
//    value itself. Nothing here touches real audio hardware; no hardware
//    proof is claimed.
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
        Assert.Null(VolumeService.MuteFromRead(E_FAIL, 1)); // even a stale "muted" bit on a failed call is unknown
    }

    [Fact]
    public void MuteAfterRead_FailedRead_KeepsCurrentPlayerMute()
    {
        Assert.True(MainWindow.MuteAfterRead(true, null));   // was muted, read failed: STAY muted
        Assert.False(MainWindow.MuteAfterRead(false, null)); // was unmuted, read failed: stay unmuted
    }

    [Fact]
    public void MuteAfterRead_SuccessfulRead_Wins()
    {
        Assert.True(MainWindow.MuteAfterRead(false, true));
        Assert.False(MainWindow.MuteAfterRead(true, false));
    }

    private static void Sta(VolumeService volume, Action<MainWindow> body)
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
                var w = new MainWindow(libvlc, player, new PlayerSettings(path), volume);
                body(w);
                w.Close();
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

    private static void InvokePoll(MainWindow w) =>
        typeof(MainWindow).GetMethod("UpdatePosition", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);

    [Fact]
    public void SharedMute_FailedRead_PreservesPlayerMute()
    {
        Sta(new VolumeService { ForcedMuteOverride = VolumeService.ForcedMute.Unknown }, w =>
        {
            w.TrackedPlayerMute = true;
            Assert.True(w.SharedMute);   // failed read must not report "unmuted"
            w.TrackedPlayerMute = false;
            Assert.False(w.SharedMute);  // and must not report "muted" either
        });
    }

    [Fact]
    public void SharedMute_SuccessfulRead_Wins()
    {
        Sta(new VolumeService { ForcedMuteOverride = VolumeService.ForcedMute.True }, w =>
        {
            w.TrackedPlayerMute = false;
            Assert.True(w.SharedMute);
        });
        Sta(new VolumeService { ForcedMuteOverride = VolumeService.ForcedMute.False }, w =>
        {
            w.TrackedPlayerMute = true;
            Assert.False(w.SharedMute);
        });
    }

    [Fact]
    public void Poll_FailedRead_KeepsPlayerMute_BothPolarities()
    {
        Sta(new VolumeService { ForcedMuteOverride = VolumeService.ForcedMute.Unknown }, w =>
        {
            w.TrackedPlayerMute = true;
            InvokePoll(w);
            Assert.True(w.TrackedPlayerMute);    // muted stays muted across a failed poll read
            w.TrackedPlayerMute = false;
            InvokePoll(w);
            Assert.False(w.TrackedPlayerMute);   // unmuted stays unmuted
        });
    }

    [Fact]
    public void Poll_SuccessfulRead_MovesPlayerMute()
    {
        Sta(new VolumeService { ForcedMuteOverride = VolumeService.ForcedMute.True }, w =>
        {
            w.TrackedPlayerMute = false;
            InvokePoll(w);
            Assert.True(w.TrackedPlayerMute);
        });
    }
}
