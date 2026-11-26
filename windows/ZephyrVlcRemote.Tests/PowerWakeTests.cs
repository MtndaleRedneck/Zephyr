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
        // Fixture state is reset BEFORE and AFTER every test: the monitor state and the swallowed-press memory are
        // process-wide, so nothing a test leaves can reach the next one.
        public ButtonReleaseTests() { Reset(); }
        public void Dispose() { Reset(); }
        private static void Reset() { PowerWake.NoteMonitorState(1); PowerWake.ForgetSwallowedButtonsForTests(); }

        [Fact]
        public void ReleaseOfAPressDeliveredWhileLit_IsNeverSwallowed_WhateverTheDisplayIsDoingLater()
        {
            foreach (var state in new uint[] { 0, 1, 2, 99 })    // off, on, dimmed, unknown
            {
                Reset();
                Assert.False(PowerWake.ShouldSwallowButton("A", "left", true));    // lit: delivered
                PowerWake.NoteMonitorState(state);                                  // the screen goes dark mid-hold
                Assert.False(PowerWake.ShouldSwallowButton("A", "left", false));   // the release still reaches the mouse
            }
        }

        [Fact]
        public void ALoneReleaseWithNoSwallowedPress_IsDelivered_WhateverTheDisplayIsDoing()
        {
            foreach (var state in new uint[] { 0, 1, 2, 99 })
            {
                Reset(); PowerWake.NoteMonitorState(state);
                Assert.False(PowerWake.ShouldSwallowButton("A", "left", false));
            }
        }

        [Fact]
        public void WakeOnlyRightTap_SwallowsThePress_AndItsRelease_ExactlyOnce()
        {
            PowerWake.NoteMonitorState(0);
            Assert.True(PowerWake.ShouldSwallowButton("A", "right", true));        // wake-on-first-press kept
            PowerWake.NoteMonitorState(1);                                          // the wake lit the screen
            Assert.True(PowerWake.ShouldSwallowButton("A", "right", false));       // its release is consumed: no lone right-up
            Assert.False(PowerWake.ShouldSwallowButton("A", "right", false));      // one match only, then forgotten
            Assert.Equal(0, PowerWake.SwallowedButtonCountForTests);
        }

        [Fact]
        public void PressStillPassesWhenLit_AndUnknownCountsAsDark()
        {
            PowerWake.NoteMonitorState(1);
            Assert.False(PowerWake.ShouldSwallowButton("A", "left", true));
            Assert.Equal(0, PowerWake.SwallowedButtonCountForTests);
            PowerWake.NoteMonitorState(99);
            Assert.True(PowerWake.ShouldSwallowButton("A", "left", true));
        }

        [Fact]
        public void TheSwallowedPressOfOnePhoneAndButton_NeverEatsAnotherPhonesOrButtonsRelease()
        {
            PowerWake.NoteMonitorState(0);
            Assert.True(PowerWake.ShouldSwallowButton("A", "left", true));
            PowerWake.NoteMonitorState(1);
            Assert.False(PowerWake.ShouldSwallowButton("B", "left", false));       // another phone
            Assert.False(PowerWake.ShouldSwallowButton("A", "right", false));      // the other button
            Assert.Equal(1, PowerWake.SwallowedButtonCountForTests);               // A's left memory is still there
            Assert.True(PowerWake.ShouldSwallowButton("A", "left", false));        // and only A's left release matches
        }

        [Fact]
        public void ANullOrUnknownButtonName_IsTheLeftButton_LikeTheMouseLayer()
        {
            PowerWake.NoteMonitorState(0);
            Assert.True(PowerWake.ShouldSwallowButton("A", null, true));
            PowerWake.NoteMonitorState(1);
            Assert.False(PowerWake.ShouldSwallowButton("A", "right", false));
            Assert.True(PowerWake.ShouldSwallowButton("A", "left", false));
        }

        [Fact]
        public void ALaterDeliveredPress_DropsAStaleMemory_SoItsOwnReleaseIsDelivered()
        {
            PowerWake.NoteMonitorState(0);
            Assert.True(PowerWake.ShouldSwallowButton("A", "left", true));         // swallowed; its release never came
            PowerWake.NoteMonitorState(1);
            Assert.False(PowerWake.ShouldSwallowButton("A", "left", true));        // a new press, lit: delivered, memory dropped
            Assert.Equal(0, PowerWake.SwallowedButtonCountForTests);
            Assert.False(PowerWake.ShouldSwallowButton("A", "left", false));       // so this release is NOT eaten
        }

        [Fact]
        public void ConcurrentPhones_KeepTheirOwnMemory()
        {
            PowerWake.NoteMonitorState(0);
            var owners = Enumerable.Range(0, 8).Select(i => "P" + i).ToArray();
            Parallel.ForEach(owners, o => Assert.True(PowerWake.ShouldSwallowButton(o, "left", true)));
            Assert.Equal(8, PowerWake.SwallowedButtonCountForTests);
            PowerWake.NoteMonitorState(1);
            var matched = 0;
            Parallel.ForEach(owners, o => { if (PowerWake.ShouldSwallowButton(o, "left", false)) Interlocked.Increment(ref matched); });
            Assert.Equal(8, matched);
            Assert.Equal(0, PowerWake.SwallowedButtonCountForTests);
        }
    }
}
