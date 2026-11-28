package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** Positive and negative cases for the cold-open dialog gate. Pure JVM logic, run on the device runner with the other suites. */
class AnrDialogGateTest {
    private class Rig(val dialogs: ArrayDeque<AnrDialogGate.Dialog?>, val windowUpAtPoll: Int = Int.MAX_VALUE) {
        var clock = 0L
        var polls = 0
        var tapped = 0
        val taps = AnrDialogGate.Taps()
        val captured = mutableListOf<Pair<Int, AnrDialogGate.Dialog>>()
        fun run(budget: Long = 20000): Boolean = AnrDialogGate.waitForWindow(
            budgetMs = budget, pollMs = 1000, taps = taps, now = { clock },
            dialog = { if (dialogs.isEmpty()) null else dialogs.removeFirst() },
            capture = { n, d -> captured.add(n to d) },
            tap = { tapped += 1 },
            windowUp = { ms -> polls += 1; clock += ms; polls >= windowUpAtPoll },
        )
    }
    private val launcher = AnrDialogGate.Dialog("Pixel Launcher isn't responding", "android")

    @Test fun classifierToleratesOnlyTheExactDialog() {
        assertEquals(AnrDialogGate.Verdict.TOLERATE, AnrDialogGate.classify("Pixel Launcher isn't responding", "android"))
        for ((t, p) in listOf(
            "Zephyr isn't responding" to "android",
            "Pixel Launcher isn't responding" to "com.zephyr.remote",
            "Pixel Launcher isn't responding" to null,
            null to "android",
            "System UI isn't responding" to "android",
            "pixel launcher isn't responding" to "android",
            "Pixel Launcher isn't responding " to "android",
            "" to "",
        )) assertEquals("$t/$p", AnrDialogGate.Verdict.REJECT, AnrDialogGate.classify(t, p))
    }

    @Test fun noDialogAndWindowUpPassesWithoutATap() {
        val r = Rig(ArrayDeque(), windowUpAtPoll = 1)
        assertTrue(r.run()); assertEquals(0, r.tapped); assertTrue(r.captured.isEmpty())
    }

    @Test fun toleratedDialogIsCapturedThenTappedThenWindowPasses() {
        val r = Rig(ArrayDeque(listOf(launcher)), windowUpAtPoll = 2)
        assertTrue(r.run()); assertEquals(1, r.tapped); assertEquals(1, r.captured.size); assertEquals(1, r.captured[0].first)
    }

    @Test fun appOwnedAnrDialogThrowsAndIsNeverTapped() {
        val r = Rig(ArrayDeque(listOf(AnrDialogGate.Dialog("Zephyr isn't responding", "android"))), windowUpAtPoll = 1)
        try { r.run(); fail("must throw") } catch (e: AssertionError) { assertTrue(e.message!!.contains("not dismissed")) }
        assertEquals(0, r.tapped)
    }

    @Test fun rightTitleFromAnotherPackageThrowsAndIsNeverTapped() {
        val r = Rig(ArrayDeque(listOf(AnrDialogGate.Dialog("Pixel Launcher isn't responding", "com.zephyr.remote"))))
        try { r.run(); fail("must throw") } catch (e: AssertionError) { }
        assertEquals(0, r.tapped)
    }

    @Test fun sixthTapThrows() {
        val r = Rig(ArrayDeque(List(10) { launcher }))
        try { r.run(); fail("must throw") } catch (e: AssertionError) { assertTrue(e.message!!.contains("keeps returning")) }
        assertEquals(AnrDialogGate.MAX_TAPS, r.tapped)
    }

    @Test fun missingWindowStillFailsAfterTheSameBudgetEvenWithDialogsTolerated() {
        val r = Rig(ArrayDeque(listOf(launcher, launcher)), windowUpAtPoll = Int.MAX_VALUE)
        assertFalse(r.run(20000))
        assertEquals(2, r.tapped)
        assertTrue("polled until the deadline: ${r.polls}", r.polls in 20..21)
    }

    @Test fun tapsDoNotExtendTheDeadline() {
        val r = Rig(ArrayDeque(List(5) { launcher }))
        assertFalse(r.run(8000))
        assertTrue("polls=${r.polls}", r.polls in 8..9)
    }

    // Persistence check (the second original assertion): immediate, no grace.
    private fun persists(rig: Rig, hasWindow: () -> Boolean) = AnrDialogGate.persists(
        taps = rig.taps, dialog = { if (rig.dialogs.isEmpty()) null else rig.dialogs.removeFirst() },
        capture = { n, d -> rig.captured.add(n to d) }, tap = { rig.tapped += 1 }, hasWindow = hasWindow)

    @Test fun persistenceWithWindowPresentNeedsNoDialogCheckAndNoTap() {
        val r = Rig(ArrayDeque(listOf(launcher)))
        assertTrue(persists(r) { true }); assertEquals(0, r.tapped); assertEquals(1, r.dialogs.size)
    }

    @Test fun persistenceNoDialogMeansTheFirstFailedQueryIsFinal() {
        val r = Rig(ArrayDeque()); var queries = 0
        assertFalse(persists(r) { queries += 1; false })
        assertEquals(1, queries); assertEquals(0, r.tapped); assertEquals(0L, r.clock)
    }

    @Test fun persistenceToleratedDialogIsTappedThenQueriedAgain() {
        val r = Rig(ArrayDeque(listOf(launcher))); var queries = 0
        assertTrue(persists(r) { queries += 1; queries >= 2 })
        assertEquals(1, r.tapped); assertEquals(2, queries); assertEquals(1, r.captured.size)
    }

    @Test fun persistenceToleratedDialogButWindowStillMissingFails() {
        val r = Rig(ArrayDeque(listOf(launcher)))
        assertFalse(persists(r) { false }); assertEquals(1, r.tapped)
    }

    @Test fun persistenceForeignDialogThrowsWithoutTap() {
        val r = Rig(ArrayDeque(listOf(AnrDialogGate.Dialog("Zephyr isn't responding", "android"))))
        try { persists(r) { false }; fail("must throw") } catch (e: AssertionError) { }
        assertEquals(0, r.tapped)
    }

    @Test fun persistenceSharesTheTapCapWithTheArrivalWait() {
        val r = Rig(ArrayDeque(List(AnrDialogGate.MAX_TAPS) { launcher }), windowUpAtPoll = AnrDialogGate.MAX_TAPS)
        assertTrue(r.run()); assertEquals(AnrDialogGate.MAX_TAPS, r.tapped)
        r.dialogs.addLast(launcher)
        try { persists(r) { false }; fail("sixth shared tap must throw") } catch (e: AssertionError) { assertTrue(e.message!!.contains("keeps returning")) }
        assertEquals(AnrDialogGate.MAX_TAPS, r.tapped)
    }
}
