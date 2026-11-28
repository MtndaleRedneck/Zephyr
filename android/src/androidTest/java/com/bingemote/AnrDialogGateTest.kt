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
        fun persists(hasWindow: () -> Boolean) = AnrDialogGate.persists(
            taps = taps, dialog = ::nextDialog, capture = ::capture, tap = ::tap, hasWindow = { queries += 1; hasWindow() })
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
        val r = Rig(listOf(launcher)); assertTrue(r.persists { r.queries >= 2 }); assertEquals(listOf("capture1", "tap"), r.events); assertEquals(2, r.queries)
    }
    @Test fun persistenceToleratedDialogButWindowStillMissingFails() { val r = Rig(listOf(launcher)); assertFalse(r.persists { false }); assertEquals(1, r.tapped) }
    @Test fun persistenceForeignDialogThrowsWithoutCaptureOrTap() {
        val r = Rig(listOf(appAnr)); expectFailure("not dismissed") { r.persists { false } }; assertEquals(emptyList<String>(), r.events)
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
    private fun verify(again: AnrDialogGate.Dialog? = launcher, nodes: Int = 1, waits: Int = 1, waitPkg: String? = "android", first: AnrDialogGate.Dialog = launcher) =
        AnrDialogGate.verifyBeforeClick(first, again, nodes, waits, waitPkg)
    @Test fun verifyAcceptsTheUnchangedExactDialog() { verify() }
    @Test fun verifyRejectsAnAppAnrDialogThatReplacedIt() { expectFailure("changed") { verify(again = appAnr) } }
    @Test fun verifyRejectsTheSameTitleFromAnotherPackageNow() { expectFailure("changed") { verify(again = AnrDialogGate.Dialog("Pixel Launcher isn't responding", "com.zephyr.remote")) } }
    @Test fun verifyRejectsADialogThatVanished() { expectFailure("vanished") { verify(again = null) } }
    @Test fun verifyRejectsAFirstReadThatWasNotTolerated() { expectFailure("not a tolerated") { verify(first = appAnr, again = appAnr) } }
    @Test fun verifyRejectsTwoDialogsOnScreen() { expectFailure("exactly one ANR dialog") { verify(nodes = 2) } }
    @Test fun verifyRejectsNoWaitOrTwoWaitsInsideTheDialog() {
        expectFailure("exactly one Wait") { verify(waits = 0) }
        expectFailure("exactly one Wait") { verify(waits = 2) }
    }
    @Test fun verifyRejectsAWaitNotOwnedByAndroid() {
        expectFailure("not owned") { verify(waitPkg = "com.zephyr.remote") }
        expectFailure("not owned") { verify(waitPkg = null) }
    }
}
