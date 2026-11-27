package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** Publication re-reads the store at execution time and publishes only a record that is still the expected one. */
class LivePublicationTest {
    private val pin = "ab".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"
    private val tok1 = "a1".repeat(32)
    private val tok2 = "c3".repeat(32)

    private class Store : SecurePairStore {
        @Volatile var cred = ""; var intentV = ""; var vlc = "v"; var name = "PC"; var legacy = ""
        override var credential: String get() = cred; set(v) { cred = v }
        override var intent: String get() = intentV; set(v) { intentV = v }
        override var vlcPassword: String get() = vlc; set(v) { vlc = v }
        override var pcName: String get() = name; set(v) { name = v }
        override var legacyToken: String get() = legacy; set(v) { legacy = v }
        override fun flush() = true
        override fun healthy() = true
    }
    @Before fun reset() { KeyChangeState.resetForTests() }
    private fun save(s: Store, host: String, tok: String) = SecurePairSave.save(s, host, 8492, "PC", "pw", SecureHttp.BoundPin(pin, tok), pc)

    private class Live { var host = ""; var port = 0; var token = ""; var calls = 0
        val set: (String, Int, String) -> Unit = { h, p, t -> host = h; port = p; token = t; calls++ } }

    @Test fun uncontendedMoveIsPublishedFromTheStoredRecord() {
        val s = Store(); save(s, "192.168.1.5", tok1); val live = Live()
        val moved = SecurePairSave.updateAddress(s, s.cred, "192.168.1.9", 9000); assertTrue(moved.usable)
        assertTrue(LivePublication.publishIfCurrent(s, s.cred, live.set))
        assertEquals(Triple("192.168.1.9", 9000, tok1), Triple(live.host, live.port, live.token))
    }

    @Test fun aRePairAfterTheMoveButBeforeTheQueuedPublicationBlocksTheStalePublication() {
        val s = Store(); save(s, "192.168.1.5", tok1); val live = Live()
        SecurePairSave.updateAddress(s, s.cred, "192.168.1.9", 9000)
        val expectedAtMove = s.cred                               // captured on the IO thread
        save(s, "192.168.1.50", tok2)                             // a re-pair lands before the Main hop runs
        assertFalse(LivePublication.publishIfCurrent(s, expectedAtMove, live.set))
        assertEquals(0, live.calls)                               // nothing published: the old identity's token never reaches live state
        assertTrue(LivePublication.publishIfCurrent(s, null, live.set))   // the re-pair's own publication reads the new record
        assertEquals(Triple("192.168.1.50", 8492, tok2), Triple(live.host, live.port, live.token))
    }

    @Test fun anUnpairedOrBlockedStorePublishesNothing() {
        val s = Store(); val live = Live()
        assertFalse(LivePublication.publishIfCurrent(s, null, live.set))
        save(s, "192.168.1.5", tok1); s.intentV = "saving"
        assertFalse(LivePublication.publishIfCurrent(s, null, live.set)); assertEquals(0, live.calls)
    }

    @Test fun saveRacingAMoveOnOneRecordLeavesExactlyOneWinnerAndACoherentFinalRecord() {
        repeat(30) {
            val s = Store(); save(s, "192.168.1.5", tok1); val expected = s.cred
            val results = java.util.Collections.synchronizedList(mutableListOf<SecurePairSave.MoveResult>())
            val go = java.util.concurrent.CountDownLatch(1); val done = java.util.concurrent.CountDownLatch(8)
            for (i in 0 until 8) Thread { go.await(); results += SecurePairSave.updateAddress(s, expected, "192.168.2.${i + 1}", 8492); done.countDown() }.apply { isDaemon = true; start() }
            go.countDown(); assertTrue(done.await(10, java.util.concurrent.TimeUnit.SECONDS))
            assertEquals(1, results.count { it == SecurePairSave.MoveResult.COMMITTED })        // one winner on the shared snapshot
            assertEquals(7, results.count { it == SecurePairSave.MoveResult.REFUSED })          // every loser saw a stale snapshot, wrote nothing
            val c = (SecurePairSave.load(s) as PairedCredential.State.Secure).credential
            assertTrue(c.host.startsWith("192.168.2.")); assertEquals(tok1, c.token)            // the winner's record, whole
        }
    }
}
