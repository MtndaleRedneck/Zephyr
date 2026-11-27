package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class MovedNoticeGateTest {
    private val NEW = "new-endpoint"
    private val OLD = "old-endpoint"

    @Test fun noNoticeBeforeAMoveThenOnlyAfterTheNextAuthenticatedPollForTenSeconds() {
        val g = MovedNoticeGate()
        assertNull(g.onAuthenticatedPoll(500L, OLD))                    // no move: nothing
        g.onDurableMove("Office PC", NEW, 1_000L)                       // a saved address alone shows nothing
        assertEquals("Office PC", g.onAuthenticatedPoll(2_000L, NEW))   // first authenticated success against the moved endpoint
        assertEquals("Office PC", g.onAuthenticatedPoll(11_999L, NEW))
        assertNull(g.onAuthenticatedPoll(12_001L, NEW))
        assertNull(g.onAuthenticatedPoll(20_000L, NEW))
    }
    @Test fun clearDropsAPendingMoveSoAFailedSessionNeverClaimsReconnected() {
        val g = MovedNoticeGate(); g.onDurableMove("Office PC", NEW, 0L); g.clear()
        assertNull(g.onAuthenticatedPoll(5L, NEW))
    }

    @Test fun clearAfterRePairDropsAShowingNoticeToo() {
        val g = MovedNoticeGate(); g.onDurableMove("Old PC", NEW, 0L)
        assertEquals("Old PC", g.onAuthenticatedPoll(1_000L, NEW))   // showing
        g.clear()                                                    // successful re-pair with a new key
        assertNull(g.onAuthenticatedPoll(2_000L, NEW))
    }

    @Test fun aPollThatUsedTheOldEndpointNeverConfirmsTheMoveButALaterOneDoes() {
        val g = MovedNoticeGate(); g.onDurableMove("Office PC", NEW, 1_000L)
        assertNull(g.onAuthenticatedPoll(1_500L, OLD))               // in-flight poll started before the move
        assertEquals("Office PC", g.onAuthenticatedPoll(2_500L, NEW))
    }

    @Test fun aPendingMoveExpiresSoAMuchLaterSuccessIsNotAnnouncedAsAMove() {
        val g = MovedNoticeGate(showMs = 10_000L, pendingMs = 60_000L); g.onDurableMove("Office PC", NEW, 0L)
        assertNull(g.onAuthenticatedPoll(60_001L, NEW))              // failed polls in between; connection recovered much later
        assertNull(g.onAuthenticatedPoll(61_000L, NEW))
    }
}
