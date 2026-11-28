package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** Positive and negative cases for the cold-open dialog gate. Pure JVM logic, run on the device runner with the other suites. */
class AnrDialogGateTest {
    /** Runs block and returns the AssertionError it threw; fails (outside any catch) if it threw none or the message lacks [contains]. */
    private fun expectFailure(contains: String, block: () -> Unit): AssertionError {
        require(contains.isNotEmpty())
        var thrown: AssertionError? = null
        try { block() } catch (e: AssertionError) { thrown = e }
        if (thrown == null) fail("expected an AssertionError containing '$contains', none was thrown")
        assertTrue("message was: ${thrown!!.message}", thrown.message!!.contains(contains))
        return thrown
    }

    private val launcher = AnrDialogGate.Dialog("Pixel Launcher isn't responding", "android")
    private val appAnr = AnrDialogGate.Dialog("Zephyr isn't responding", "android")

    private class Rig(dialogs: List<AnrDialogGate.Dialog?>, val windowUpAtPoll: Int = Int.MAX_VALUE) {
        val dialogs = ArrayDeque(dialogs)
        val taps = AnrDialogGate.Taps()
        var clock = 0L; var polls = 0; var tapped = 0; var queries = 0
        val events = mutableListOf<String>()
        var captureFails = false
        var tapFails = false
        fun nextDialog() = if (dialogs.isEmpty()) null else dialogs.removeFirst()
        fun capture(n: Int, d: AnrDialogGate.Dialog) { events.add("capture$n"); if (captureFails) throw AssertionError("cannot capture") }
        fun tap(d: AnrDialogGate.Dialog) { events.add("tap"); if (tapFails) throw AssertionError("tap failed"); tapped += 1 }
        fun wait(budget: Long = 20000): Boolean = AnrDialogGate.waitForWindow(
            budgetMs = budget, pollMs = 1000, taps = taps, now = { clock }, dialog = ::nextDialog, capture = ::capture, tap = ::tap,
            windowUp = { ms -> polls += 1; clock += ms; polls >= windowUpAtPoll })
        var afterTapFails = false
        fun afterTap(d: AnrDialogGate.Dialog) { events.add("removed"); if (afterTapFails) throw AssertionError("removal failed") }
        fun persists(hasWindow: () -> Boolean) = AnrDialogGate.persists(
            taps = taps, dialog = ::nextDialog, capture = ::capture, tap = ::tap, afterTap = ::afterTap,
            hasWindow = { events.add("query"); queries += 1; hasWindow() })
    }

    @Test fun classifierToleratesOnlyTheExactDialog() {
        assertEquals(AnrDialogGate.Verdict.TOLERATE, AnrDialogGate.classify("Pixel Launcher isn't responding", "android"))
        for ((t, p) in listOf(
            "Zephyr isn't responding" to "android", "Pixel Launcher isn't responding" to "com.zephyr.remote",
            "Pixel Launcher isn't responding" to null, null to "android", "System UI isn't responding" to "android",
            "pixel launcher isn't responding" to "android", "Pixel Launcher isn't responding " to "android", "" to "",
            "2 ANR dialogs on screen" to null,
        )) assertEquals("$t/$p", AnrDialogGate.Verdict.REJECT, AnrDialogGate.classify(t, p))
    }

    // Arrival wait
    @Test fun noDialogAndWindowUpPassesWithoutATap() { val r = Rig(emptyList(), 1); assertTrue(r.wait()); assertEquals(emptyList<String>(), r.events) }
    @Test fun toleratedDialogIsCapturedThenTappedInThatOrderThenWindowPasses() {
        val r = Rig(listOf(launcher), 2); assertTrue(r.wait()); assertEquals(listOf("capture1", "tap"), r.events)
    }
    @Test fun appAnrDialogThrowsBeforeAnyCaptureOrTap() {
        val r = Rig(listOf(appAnr), 1); expectFailure("not dismissed") { r.wait() }; assertEquals(emptyList<String>(), r.events)
    }
    @Test fun rightTitleFromAnotherPackageThrowsBeforeAnyCaptureOrTap() {
        val r = Rig(listOf(AnrDialogGate.Dialog("Pixel Launcher isn't responding", "com.zephyr.remote")))
        expectFailure("not dismissed") { r.wait() }; assertEquals(emptyList<String>(), r.events)
    }
    @Test fun nullPackageAndNullTitleAreRejected() {
        expectFailure("not dismissed") { Rig(listOf(AnrDialogGate.Dialog("Pixel Launcher isn't responding", null))).wait() }
        expectFailure("not dismissed") { Rig(listOf(AnrDialogGate.Dialog(null, "android"))).wait() }
    }
    @Test fun multipleCandidateDialogsMarkerIsRejectedWithoutTap() {
        val r = Rig(listOf(AnrDialogGate.Dialog("2 ANR dialogs on screen", null)))
        expectFailure("not dismissed") { r.wait() }; assertEquals(0, r.tapped)
    }
    @Test fun failedCaptureThrowsBeforeTheTap() {
        val r = Rig(listOf(launcher), 1); r.captureFails = true
        expectFailure("cannot capture") { r.wait() }; assertEquals(listOf("capture1"), r.events); assertEquals(0, r.tapped)
    }
    @Test fun failedTapThrowsAndTheWindowIsNeverQueriedAfterIt() {
        val r = Rig(listOf(launcher), 1); r.tapFails = true
        expectFailure("tap failed") { r.wait() }; assertEquals(0, r.polls)
    }
    @Test fun sixthTapThrows() {
        val r = Rig(List(10) { launcher })
        expectFailure("keeps returning") { r.wait() }; assertEquals(AnrDialogGate.MAX_TAPS, r.tapped)
    }
    @Test fun missingWindowStillFailsAfterTheSameBudgetEvenWithDialogsTolerated() {
        val r = Rig(listOf(launcher, launcher)); assertFalse(r.wait(20000)); assertEquals(2, r.tapped)
        assertTrue("polled until the deadline: ${r.polls}", r.polls in 20..21)
    }
    @Test fun tapsDoNotExtendTheDeadline() { val r = Rig(List(5) { launcher }); assertFalse(r.wait(8000)); assertTrue("polls=${r.polls}", r.polls in 8..9) }

    // Persistence check (second original assertion): immediate, no grace
    @Test fun persistenceWithWindowPresentNeedsNoDialogCheckAndNoTap() {
        val r = Rig(listOf(launcher)); assertTrue(r.persists { true }); assertEquals(0, r.tapped); assertEquals(1, r.dialogs.size)
    }
    @Test fun persistenceNoDialogMeansTheFirstFailedQueryIsFinal() {
        val r = Rig(emptyList()); assertFalse(r.persists { false }); assertEquals(1, r.queries); assertEquals(0, r.tapped); assertEquals(0L, r.clock)
    }
    @Test fun persistenceToleratedDialogIsCapturedTappedThenQueriedAgain() {
        val r = Rig(listOf(launcher)); assertTrue(r.persists { r.queries >= 2 }); assertEquals(listOf("query", "capture1", "tap", "removed", "query"), r.events); assertEquals(2, r.queries)
    }
    @Test fun persistenceToleratedDialogButWindowStillMissingFails() { val r = Rig(listOf(launcher)); assertFalse(r.persists { false }); assertEquals(1, r.tapped) }
    @Test fun persistenceForeignDialogThrowsWithoutCaptureOrTap() {
        val r = Rig(listOf(appAnr)); expectFailure("not dismissed") { r.persists { false } }; assertEquals(listOf("query"), r.events)
    }
    @Test fun persistenceFailedTapDoesNotEarnAnotherQuery() {
        val r = Rig(listOf(launcher)); r.tapFails = true
        expectFailure("tap failed") { r.persists { false } }; assertEquals(1, r.queries)
    }
    @Test fun persistenceFailedCaptureThrowsBeforeTheTap() {
        val r = Rig(listOf(launcher)); r.captureFails = true
        expectFailure("cannot capture") { r.persists { false } }; assertEquals(0, r.tapped); assertEquals(1, r.queries)
    }
    @Test fun persistenceSharesTheTapCapWithTheArrivalWait() {
        val r = Rig(List(AnrDialogGate.MAX_TAPS) { launcher }, windowUpAtPoll = AnrDialogGate.MAX_TAPS)
        assertTrue(r.wait()); assertEquals(AnrDialogGate.MAX_TAPS, r.tapped)
        r.dialogs.addLast(launcher)
        expectFailure("keeps returning") { r.persists { false } }; assertEquals(AnrDialogGate.MAX_TAPS, r.tapped)
    }

    // The last gate before the click (race and replacement cases)
    private val pb = AnrDialogGate.Box(70, 1021, 1010, 1443)
    private fun panel(panelId: String? = AnrDialogGate.PANEL_ID, panelBounds: AnrDialogGate.Box? = pb, titleId: String? = AnrDialogGate.TITLE_ID,
                      titleBounds: AnrDialogGate.Box? = AnrDialogGate.Box(133, 1068, 947, 1131), waits: Int = 1, waitPkg: String? = "android",
                      waitId: String? = AnrDialogGate.WAIT_ID, waitBounds: AnrDialogGate.Box? = AnrDialogGate.Box(70, 1296, 1010, 1422)) =
        AnrDialogGate.Panel(panelId, panelBounds, titleId, titleBounds, waits, waitPkg, waitId, waitBounds)
    private fun verify(again: AnrDialogGate.Dialog? = launcher, nodes: Int = 1, p: AnrDialogGate.Panel? = panel(), first: AnrDialogGate.Dialog = launcher) =
        AnrDialogGate.verifyBeforeClick(first, again, nodes, p)
    @Test fun verifyAcceptsTheUnchangedExactDialog() { verify() }
    @Test fun verifyRejectsAnAppAnrDialogThatReplacedIt() { expectFailure("changed") { verify(again = appAnr) } }
    @Test fun verifyRejectsTheSameTitleFromAnotherPackageNow() { expectFailure("changed") { verify(again = AnrDialogGate.Dialog("Pixel Launcher isn't responding", "com.zephyr.remote")) } }
    @Test fun verifyRejectsADialogThatVanished() { expectFailure("vanished") { verify(again = null) } }
    @Test fun verifyRejectsAFirstReadThatWasNotTolerated() { expectFailure("not a tolerated") { verify(first = appAnr, again = appAnr) } }
    @Test fun verifyRejectsTwoDialogsOnScreen() { expectFailure("exactly one ANR dialog") { verify(nodes = 2) } }
    @Test fun verifyRejectsNoWaitOrTwoWaitsInsideThePanel() {
        expectFailure("exactly one Wait") { verify(p = panel(waits = 0)) }
        expectFailure("exactly one Wait") { verify(p = panel(waits = 2)) }
    }
    @Test fun verifyRejectsAWaitNotOwnedByAndroid() {
        expectFailure("not owned") { verify(p = panel(waitPkg = "com.zephyr.remote")) }
        expectFailure("not owned") { verify(p = panel(waitPkg = null)) }
    }
    // Panel boundary: no panel above the title (ancestor walk would have reached the window root) means no search and no click.
    @Test fun verifyRejectsWhenNoPanelWasFound() { expectFailure("no dialog panel") { verify(p = null) } }
    @Test fun verifyRejectsAWrongOrMissingPanelId() {
        expectFailure("no dialog panel") { verify(p = panel(panelId = "android:id/content")) }
        expectFailure("no dialog panel") { verify(p = panel(panelId = null)) }
        expectFailure("no dialog panel") { verify(p = panel(panelBounds = null)) }
    }
    @Test fun verifyRejectsATitleThatIsNotTheAlertTitleOrOutsideThePanel() {
        expectFailure("not android:id/alertTitle") { verify(p = panel(titleId = null)) }
        expectFailure("title bounds") { verify(p = panel(titleBounds = AnrDialogGate.Box(0, 0, 50, 50))) }
        expectFailure("title bounds") { verify(p = panel(titleBounds = null)) }
    }
    @Test fun verifyRejectsAForeignAndroidWaitOutsideOrWithoutTheRightId() {
        expectFailure("Wait bounds") { verify(p = panel(waitBounds = AnrDialogGate.Box(0, 2000, 1080, 2100))) }
        expectFailure("Wait bounds") { verify(p = panel(waitBounds = null)) }
        expectFailure("Wait bounds") { verify(p = panel(waitBounds = AnrDialogGate.Box(500, 500, 500, 600))) }
        expectFailure("is not android:id/aerr_wait") { verify(p = panel(waitId = "android:id/button1")) }
        expectFailure("is not android:id/aerr_wait") { verify(p = panel(waitId = null)) }
    }

    // Removal completion after the real tap
    @Test fun persistenceOrderIsQueryCaptureTapRemovedQuery() {
        val r = Rig(listOf(launcher)); var n = 0
        assertTrue(r.persists { n += 1; n >= 2 })
        assertEquals(listOf("query", "capture1", "tap", "removed", "query"), r.events)
    }
    @Test fun failedRemovalConfirmationEarnsNoQuery() {
        val r = Rig(listOf(launcher)); r.afterTapFails = true
        expectFailure("removal failed") { r.persists { false } }; assertEquals(1, r.queries)
    }
    @Test fun noRemovalWaitWhenNoDialogWasTapped() {
        val r = Rig(emptyList()); assertFalse(r.persists { false }); assertEquals(listOf("query"), r.events)
    }
    private class Removal(val goneAtSlice: Int = Int.MAX_VALUE, screens: List<AnrDialogGate.Dialog?> = emptyList()) {
        var clock = 0L; var slices = 0; var apps = 0
        val screens = ArrayDeque(screens)
        fun run(tapped: AnrDialogGate.Dialog) = AnrDialogGate.awaitRemoval(
            tapped = tapped, now = { clock },
            goneWait = { ms -> slices += 1; clock += ms; slices >= goneAtSlice },
            dialog = { if (screens.isEmpty()) tapped else screens.removeFirst() })
    }
    @Test fun removalCompletesWhenNothingIsLeftOnScreen() {
        val r = Removal(goneAtSlice = 3, screens = listOf(launcher, launcher, null)); r.run(launcher); assertEquals(3, r.slices)
    }
    @Test fun removalCompletesWhenTheDialogIsNoLongerThereAfterASlice() {
        val r = Removal(screens = listOf(launcher, null)); r.run(launcher); assertEquals(2, r.slices)
    }
    @Test fun removalTimesOutAtTheCeilingAndDoesNotExtendIt() {
        val r = Removal(); expectFailure("not removed within 5000") { r.run(launcher) }
        assertEquals(AnrDialogGate.REMOVAL_CEILING_MS / AnrDialogGate.REMOVAL_SLICE_MS, r.slices.toLong())
    }
    @Test fun removalFailsWhenAnAppAnrReplacesTheTappedDialog() {
        val r = Removal(screens = listOf(appAnr)); expectFailure("different ANR dialog") { r.run(launcher) }; assertEquals(1, r.slices)
    }
    @Test fun removalFailsWhenTheSameTitleFromAnotherPackageReplacesIt() {
        val r = Removal(screens = listOf(AnrDialogGate.Dialog("Pixel Launcher isn't responding", "com.zephyr.remote")))
        expectFailure("different ANR dialog") { r.run(launcher) }
    }
    @Test fun removalFailsWhenAnotherDialogAppearsRightAfterGone() {
        val r = Removal(goneAtSlice = 1, screens = listOf(appAnr)); expectFailure("on screen again") { r.run(launcher) }
    }
    @Test fun removalFailsOnAMultiDialogMarker() {
        val r = Removal(screens = listOf(AnrDialogGate.Dialog("2 ANR dialogs on screen", null))); expectFailure("different ANR dialog") { r.run(launcher) }
    }
}
