package com.bingemote.robo

import android.content.Context
import com.bingemote.*
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import kotlin.coroutines.CoroutineContext

/**
 * The real RemoteState wiring for the Player address-follow over the real Store (Robolectric-simulated preferences, no Keystore, no network).
 * The outcomes come from the real ZephyrMove.attempt against the real store writer; RemoteState applies them. Runs in the separate robolectric task.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class ZephyrFollowWiringTest {
    private val ctx: Context get() = RuntimeEnvironment.getApplication()
    private val pin = "ab".repeat(32); private val pin2 = "cd".repeat(32)
    private val tok = "a1".repeat(32); private val tok2 = "b2".repeat(32)

    private class Never : CoroutineDispatcher() { override fun dispatch(context: CoroutineContext, block: Runnable) { /* never stepped */ } }

    @Before @After fun clean() { Store.resetZephyrRecordForTests(); KeyChangeState.resetForTests(); PairingPublication.resetForTests() }

    private class Rig(val store: Store, val remote: RemoteState)
    private fun rig(tag: String): Rig {
        val plain = RuntimeEnvironment.getApplication().getSharedPreferences("zf-plain-$tag", 0); plain.edit().clear().commit()
        val secrets = RuntimeEnvironment.getApplication().getSharedPreferences("zf-secret-$tag", 0); secrets.edit().clear().commit()
        val store = Store(plain, secrets)
        assertEquals(SecurePairSave.Outcome.SAVED, store.saveZephyrPairing(SecureHttp.BoundPin(pin, tok), "192.168.1.5", 47823, "Office PC"))
        store.selectedSource = Source.ZEPHYR
        val remote = RemoteState(ctx, store, CoroutineScope(SupervisorJob() + Never()), null, Never())
        return Rig(store, remote)
    }
    private fun h(host: String) = DiscoveredHelper(host, 47823, "Office PC")
    private fun move(r: Rig, found: List<DiscoveredHelper>, alive: Boolean = false, verifies: Boolean = true): Pair<Store.ZephyrTarget, ZephyrMove.Outcome> {
        val before = r.store.zephyrTarget()!!
        return before to ZephyrMove.attempt(r.store.zephyrMovePort(), pingSaved = { alive }, discover = { found }, verify = { _, _, _ -> verifies })
    }
    private fun key(host: String, token: String) = HelperRediscover.addressKey(host, 47823) + "|" + token
    private fun target(r: Rig) = r.store.zephyrTarget()!!

    @Test fun ownMoveIsAcceptedNoEarlyNoticeThenShownOnlyAfterAnAuthenticatedPollOnTheMovedEndpoint() {
        val r = rig("own"); val (before, out) = move(r, listOf(h("192.168.1.77")))
        r.remote.applyZephyrMove(before, out)
        assertEquals("192.168.1.77", target(r).host)                       // the shared record was republished with the new address
        assertNull("no early notice: nothing authenticated yet", r.remote.zephyrMovedTo)
        r.remote.onZephyrAuthenticatedPoll(before, now = System.currentTimeMillis())   // a poll that used the OLD endpoint never confirms
        assertNull(r.remote.zephyrMovedTo)
        val now = System.currentTimeMillis()
        r.remote.onZephyrAuthenticatedPoll(target(r), now)
        assertEquals("Office PC", r.remote.zephyrMovedTo)
        r.remote.onZephyrAuthenticatedPoll(target(r), now + 11_000)         // read for ~10 s, then gone, and never announced again
        assertNull(r.remote.zephyrMovedTo)
        r.remote.onZephyrAuthenticatedPoll(target(r), now + 12_000)
        assertNull(r.remote.zephyrMovedTo)
    }

    @Test fun aMoveNoLongerConfirmedAfterTheWindowIsNeverAnnounced() {
        val r = rig("late"); val (before, out) = move(r, listOf(h("192.168.1.77")))
        r.remote.applyZephyrMove(before, out)
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis() + 61_000)
        assertNull(r.remote.zephyrMovedTo)
    }

    @Test fun b3NoUnverifiedResultResetsTheFailureCountOrTheCantReachState() {
        val r = rig("b3")
        r.remote.zephyrNoteFailure(); r.remote.zephyrNoteFailure()
        assertEquals(2, r.remote.zephyrFailureCount); assertTrue(r.remote.unreachable)
        val before = target(r)
        // spoofed discovery: only the saved address, pin-authenticated check fails
        val spoof = ZephyrMove.attempt(r.store.zephyrMovePort(), pingSaved = { false }, discover = { listOf(h("192.168.1.5")) }, verify = { _, _, _ -> org.junit.Assert.fail("no probe"); false })
        // a different key at a new address
        val none = ZephyrMove.attempt(r.store.zephyrMovePort(), pingSaved = { false }, discover = { listOf(h("192.168.1.66")) }, verify = { _, _, _ -> false })
        for (o in listOf(spoof, none, ZephyrMove.Outcome.Refused, ZephyrMove.Outcome.Blocked, ZephyrMove.Outcome.NotPaired, ZephyrMove.Outcome.SavedEndpointSeen)) {
            r.remote.applyZephyrMove(before, o)
            assertEquals("outcome ${o.javaClass.simpleName} must not reset", 2, r.remote.zephyrFailureCount)
            assertTrue(r.remote.unreachable)
        }
        assertEquals(before.host, target(r).host)
    }

    @Test fun b4aVerifiedMoveResetsTheOutageButTheNoticeStillWaitsForThePoll() {
        val r = rig("b4")
        r.remote.zephyrNoteFailure(); r.remote.zephyrNoteFailure(); assertTrue(r.remote.unreachable)
        val (before, out) = move(r, listOf(h("192.168.1.77")))
        r.remote.applyZephyrMove(before, out)
        assertEquals(0, r.remote.zephyrFailureCount); assertFalse(r.remote.unreachable)
        assertNull(r.remote.zephyrMovedTo)
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis())
        assertEquals("Office PC", r.remote.zephyrMovedTo)
    }

    @Test fun anExternalMutationDuringTheAttemptIsRejectedWithNoNoticeAndTheWinnerUntouched() {
        val r = rig("ext")
        val before = r.store.zephyrTarget()!!
        val out = ZephyrMove.attempt(r.store.zephyrMovePort(), pingSaved = { false }, discover = { listOf(h("192.168.1.77")) }, verify = { _, _, _ -> true })
        // a re-pair lands after our write and before RemoteState applies it
        assertEquals(SecurePairSave.Outcome.SAVED, r.store.saveZephyrPairing(SecureHttp.BoundPin(pin2, tok2), "192.168.1.9", 47823, "Other PC"))
        r.remote.zephyrNoteFailure(); r.remote.zephyrNoteFailure()
        val winner = target(r)
        r.remote.applyZephyrMove(before, out)
        assertEquals(2, r.remote.zephyrFailureCount)                        // nothing about the old PC was applied
        assertTrue(winner.same(r.store.zephyrTarget()))
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis())
        assertNull(r.remote.zephyrMovedTo)
    }

    @Test fun anImpostorOnlyEarnsAPairAgainLabelNeverAMove() {
        val r = rig("imp"); val (before, out) = move(r, listOf(h("192.168.1.66")), verifies = false)
        r.remote.applyZephyrMove(before, out)
        assertEquals("Office PC", r.remote.zephyrPairAgainFor); assertNull(r.remote.zephyrMovedTo)
        assertEquals("192.168.1.5", target(r).host)
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis())   // a good poll clears the hint
        assertNull(r.remote.zephyrPairAgainFor)
    }

    @Test fun c2aSavedPlayerPairingClearsAPendingAndAShowingNoticeAndTheHint() {
        val r = rig("c2"); val (before, out) = move(r, listOf(h("192.168.1.77")))
        r.remote.applyZephyrMove(before, out)
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis())
        assertEquals("Office PC", r.remote.zephyrMovedTo)
        r.remote.onZephyrRePaired()
        assertNull(r.remote.zephyrMovedTo); assertNull(r.remote.zephyrPairAgainFor)
        // and a pending (not yet shown) one: a re-pair drops it too
        val (b2, o2) = move(r, listOf(h("192.168.1.88")))
        r.remote.applyZephyrMove(b2, o2); r.remote.onZephyrRePaired()
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis())
        assertNull(r.remote.zephyrMovedTo)
    }

    @Test fun theHelperRePairHookDoesNotStandInForThePlayerOne() {
        val r = rig("c2h"); val (before, out) = move(r, listOf(h("192.168.1.77")))
        r.remote.applyZephyrMove(before, out)
        r.remote.onRePaired()                                               // the helper's hook
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis())
        assertEquals("Office PC", r.remote.zephyrMovedTo)                   // still pending: only onZephyrRePaired clears the Player's
    }

    @Test fun thePlayerNoticeIsNotShownWhenTheSourceIsNotThePlayer() {
        val r = rig("src"); val (before, out) = move(r, listOf(h("192.168.1.77")))
        r.store.selectedSource = Source.VLC
        r.remote.applyZephyrMove(before, out)
        r.store.selectedSource = Source.ZEPHYR
        r.remote.onZephyrAuthenticatedPoll(target(r), System.currentTimeMillis())
        assertNull(r.remote.zephyrMovedTo)
    }
}
