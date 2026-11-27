package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger

/** The rediscovery attempt as wired in RemoteState, driven with injected network calls and a fault-injectable store. */
class HelperMoveTest {
    private val pin = "ab".repeat(32)
    private val tok = "a1".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"

    private class Store : SecurePairStore {
        var cred = ""; var intentV = ""; var vlc = "vlc"; var name = "Office PC"; var legacy = ""
        var failCommit = false; var commits = 0
        override var credential: String get() = cred; set(v) { commits++; cred = v; if (failCommit) throw IllegalStateException("commit refused") }
        override var intent: String get() = intentV; set(v) { intentV = v }
        override var vlcPassword: String get() = vlc; set(v) { vlc = v }
        override var pcName: String get() = name; set(v) { name = v }
        override var legacyToken: String get() = legacy; set(v) { legacy = v }
        override fun flush() = true
        override fun healthy() = true
    }

    @Before fun reset() { KeyChangeState.resetForTests() }

    private fun paired(host: String = "192.168.1.5"): Store {
        val s = Store(); SecurePairSave.save(s, host, 8492, "Office PC", "pw", SecureHttp.BoundPin(pin, tok), pc); s.commits = 0; return s
    }
    private fun h(host: String, port: Int = 8492, name: String = "Office PC") = DiscoveredHelper(host, port, name)
    private fun verifyOk(rec: String, host: String, port: Int) = SecureHttp.VerifiedAddress(pc, host, port, rec)

    @Test fun savedEndpointAnsweringMeansNoDiscoveryAndNoMove() {
        val s = paired(); var discovered = false
        val out = HelperMove.attempt(s, pingSaved = { true }, discover = { discovered = true; emptyList() })
        assertTrue(out === HelperMove.Outcome.SavedEndpointSeen); assertFalse(discovered); assertEquals(0, s.commits)
    }

    @Test fun savedEndpointListedInOtherTextFormsIsNotAMove() {
        val s = paired("fd00::1")
        val out = HelperMove.attempt(s, pingSaved = { false }, discover = { listOf(h("[FD00:0:0:0:0:0:0:1]")) },
            verify = { r, a, p -> fail("must not probe the saved endpoint $a:$p"); null })
        assertTrue(out === HelperMove.Outcome.SavedEndpointSeen); assertEquals(0, s.commits)
    }

    @Test fun verifiedMoveWritesOnceAndReturnsTheFreshStoredRecord() {
        val s = paired()
        val seenTokens = mutableListOf<String>()
        val out = HelperMove.attempt(s, pingSaved = { c -> seenTokens += c.host; false }, discover = { listOf(h("192.168.1.77")) },
            verify = { r, a, p -> verifyOk(r, a, p) })
        out as HelperMove.Outcome.Moved
        assertTrue(out.committed); assertEquals("192.168.1.77", out.credential.host); assertEquals(tok, out.credential.token)
        assertEquals(1, s.commits); assertEquals("Office PC", out.pcName)
        assertEquals(listOf("192.168.1.5"), seenTokens)                      // the saved-endpoint ping is the only token user, and only for the saved host
        assertEquals(out.credential.encode(), s.cred)                        // returned record is the stored one
    }

    @Test fun anUnconfirmedCommitIsUsableButNotDurableSoNoNotice() {
        val s = paired(); s.failCommit = true
        val out = HelperMove.attempt(s, pingSaved = { false }, discover = { listOf(h("192.168.1.77")) }, verify = { r, a, p -> verifyOk(r, a, p) })
        out as HelperMove.Outcome.Moved
        assertFalse(out.committed); assertEquals("192.168.1.77", out.credential.host)
        assertFalse(SecurePairSave.isLatched(s))
    }

    @Test fun noKeyMatchMovesNothing() {
        val s = paired()
        val out = HelperMove.attempt(s, pingSaved = { false }, discover = { listOf(h("192.168.1.77"), h("192.168.1.78")) }, verify = { _, _, _ -> null })
        assertTrue(out === HelperMove.Outcome.NoneVerified); assertEquals(0, s.commits)
    }

    @Test fun nonPrivateOrNameCandidatesAreNeverProbedAndAtMostThreeAre() {
        val s = paired(); val probed = mutableListOf<String>()
        val found = listOf(h("8.8.8.8"), h("evil.example"), h("192.168.1.71"), h("192.168.1.72"), h("192.168.1.73"), h("192.168.1.74"))
        HelperMove.attempt(s, pingSaved = { false }, discover = { found }, verify = { _, a, _ -> probed += a; null })
        assertEquals(listOf("192.168.1.71", "192.168.1.72", "192.168.1.73"), probed)
    }

    @Test fun aStaleSnapshotIsRefusedWithoutAWrite() {
        val s = paired()
        val out = HelperMove.attempt(s, pingSaved = { false }, discover = { listOf(h("192.168.1.77")) },
            verify = { r, a, p ->
                // a re-pair lands between the probe and the write
                s.cred = PairedCredential.of(pc, pin, "192.168.1.200", 8492, "c3".repeat(32))!!.encode(); s.commits = 0
                verifyOk(r, a, p)
            })
        assertTrue(out === HelperMove.Outcome.Refused); assertEquals(0, s.commits)
        assertEquals("192.168.1.200", (SecurePairSave.load(s) as PairedCredential.State.Secure).credential.host)
    }

    @Test fun anUnreadableOrUnpairedStoreIsNotPairedAndSendsNothing() {
        val s = Store(); var pinged = false
        val out = HelperMove.attempt(s, pingSaved = { pinged = true; false }, discover = { emptyList() })
        assertTrue(out === HelperMove.Outcome.NotPaired); assertFalse(pinged)
    }

    @Test fun twoAttemptsBehindOneAdmissionRunOnce() {
        val a = RediscoverAdmission(clock = { 10L }); val runs = AtomicInteger()
        repeat(2) { if (a.tryAdmit()) { try { runs.incrementAndGet() } finally { a.release() } } }
        assertEquals(1, runs.get())
    }

    private fun fail(msg: String) { org.junit.Assert.fail(msg) }
}
