using ZephyrVlcRemote;
using Xunit;

namespace ZephyrVlcRemote.Tests;

// The three outer tests change PowerWake's process-wide hold and monitor state, so they join the same serial
// collection as every other test that touches shared helper state (they used to run in parallel with it).
[Collection("ProgramState")]
public class PowerWakeTests
{
    [Fact]
    public void SessionHoldFollowsVlc()
    {
        PowerWake.SyncToVlc(true);
        Assert.True(PowerWake.HoldingForVlc);
        PowerWake.SyncToVlc(true); // steady state, no flip
        Assert.True(PowerWake.HoldingForVlc);
        PowerWake.SyncToVlc(false);
        Assert.False(PowerWake.HoldingForVlc);
    }

    [Fact]
    public void ReleaseDropsTheHold()
    {
        PowerWake.SyncToVlc(true);
        PowerWake.Release();
        Assert.False(PowerWake.HoldingForVlc);
    }

    [Fact]
    public void DisplayWakeIsOneShotAndKeepsNoHold()
    {
        PowerWake.Release();
        PowerWake.WakeDisplay();
        Assert.False(PowerWake.HoldingForVlc);
    }

    [Collection("ProgramState")]
    public class ButtonReleaseTests : IDisposable
    {
        public ButtonReleaseTests() { PowerWake.NoteMonitorState(1); }
        public void Dispose() { PowerWake.NoteMonitorState(1); }

        [Fact]
        public void ReleaseIsNeverSwallowed_WhateverTheDisplayIsDoing()
        {
            foreach (var state in new uint[] { 0, 1, 2, 99 })    // off, on, dimmed, unknown
            {
                PowerWake.NoteMonitorState(state);
                Assert.False(PowerWake.ShouldSwallowButton(false));
            }
        }

        [Fact]
        public void PressStillOnlyWakesWhenDark_AndPassesWhenLit()
        {
            PowerWake.NoteMonitorState(0);
            Assert.True(PowerWake.ShouldSwallowButton(true));     // wake-on-first-press kept
            PowerWake.NoteMonitorState(1);
            Assert.False(PowerWake.ShouldSwallowButton(true));
        }
    }
}
