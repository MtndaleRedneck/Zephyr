package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import java.util.concurrent.CancellationException

/** Player address-follow on in-memory fakes: the store protocol (C1, CAS) and the attempt (Amendment B, B2). JVM logic only. */
class ZephyrMoveTest {
    private val pin = "ab".repeat(32); private val pin2 = "cd".repeat(32)
    private val tok = "a1".repeat(32); private val tok2 = "b2".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"; private val pc2 = "f".repeat(32)

    /** Zephyr store fake. [onCredentialRead] runs on every read of the record (to simulate a writer landing between steps). */
    private class Disk {
        var cred = ""; var intentV = ""; var name = "Office PC"
        var failCommit = false; var commits = 0; var healthy = true
        var reads = 0
        var onCredentialRead: (() -> Unit)? = null
        val store = object : ZephyrPairStore {
            override var credential: String
                get() { reads++; onCredentialRead?.invoke(); return cred }
                set(v) { commits++; cred = v; if (failCommit) throw IllegalStateException("commit refused") }
            override var intent: String get() = intentV; set(v) { intentV = v }
            override var pcName: String get() = name; set(v) { name = v }
            override fun flush() = true
            override fun healthy() = healthy
        }
    }

    @Before fun reset() { KeyChangeState.resetForTests() }

    private fun paired(host: String = "192.168.1.5"): Disk {
        val d = Disk(); ZephyrPairSave.save(d.store, host, 47823, "Office PC", SecureHttp.BoundPin(pin, tok), pc); d.commits = 0; d.reads = 0; return d
    }
    private fun port(d: Disk, afterCommit: (ZephyrMove.Commit) -> Unit = {}) = object : ZephyrMove.Port {
        override fun snapshot(): ZephyrMove.Snapshot? {
            val c = (ZephyrPairSave.load(d.store) { false } as? ZephyrCredential.State.Secure)?.credential ?: return null
            return ZephyrMove.Snapshot(c, c.encode(), d.name)
        }
        override fun commit(v: SecureHttp.VerifiedAddress) = ZephyrPairSave.commitMove(d.store, v).also(afterCommit)
    }
    private fun h(host: String, p: Int = 47823, name: String = "Office PC") = DiscoveredHelper(host, p, name)
    private fun verified(d: Disk, host: String, p: Int = 47823) = SecureHttp.VerifiedAddress(pc, host, p, d.cred)
    private fun secure(d: Disk) = (ZephyrPairSave.load(d.store) { false } as ZephyrCredential.State.Secure).credential

    // ---- store: CAS, refusal, byte identity (tests 1, 2) ----
    @Test fun aVerifiedMoveChangesOnlyHostAndPort() {
        val d = paired(); val old = secure(d)
        val r = ZephyrPairSave.commitMove(d.store, verified(d, "192.168.1.77", 47900)) as ZephyrMove.Commit.Written
        assertTrue(r.committed)
        val c = secure(d)
        assertEquals(listOf(old.pcId, old.pinHex, old.token), listOf(c.pcId, c.pinHex, c.token))
        assertEquals(listOf("192.168.1.77", 47900), listOf(c.host, c.port))
        assertEquals(c.encode(), r.credential.encode()); assertEquals("", d.intentV)
        assertEquals(1, d.commits)
    }

    @Test fun aStaleExpectedRecordIsRefusedAndNothingIsWritten() {
        val d = paired(); val v = verified(d, "192.168.1.77")
        // a re-pair lands during the probe
        ZephyrPairSave.save(d.store, "192.168.1.9", 47823, "Other", SecureHttp.BoundPin(pin2, tok2), pc2); d.commits = 0
        val winner = d.cred
        assertTrue(ZephyrPairSave.commitMove(d.store, v) === ZephyrMove.Commit.Refused)
        assertEquals(winner, d.cred); assertEquals(0, d.commits)
    }

    @Test fun anotherMoveDuringTheProbeInvalidatesTheSnapshot() {
        val d = paired(); val v = verified(d, "192.168.1.77")
        assertTrue(ZephyrPairSave.commitMove(d.store, verified(d, "192.168.1.88")) is ZephyrMove.Commit.Written)
        val winner = d.cred; d.commits = 0
        assertTrue(ZephyrPairSave.commitMove(d.store, v) === ZephyrMove.Commit.Refused)
        assertEquals(winner, d.cred); assertEquals(0, d.commits)
    }

    @Test fun keyChangedLatchedIntentBusyAndUnhealthyAreRefused() {
        var d = paired(); val v = verified(d, "192.168.1.77")
        KeyChangeState.mark(pc); assertTrue(ZephyrPairSave.commitMove(d.store, v) === ZephyrMove.Commit.Refused); assertEquals(0, d.commits)
        KeyChangeState.resetForTests()
        d = paired(); d.intentV = "saving"; assertTrue(ZephyrPairSave.commitMove(d.store, verified(d, "192.168.1.77")) === ZephyrMove.Commit.Refused)
        d = paired(); d.healthy = false; assertTrue(ZephyrPairSave.commitMove(d.store, verified(d, "192.168.1.77")) === ZephyrMove.Commit.Refused)
        d = paired(); d.healthy = true
        d.failCommit = true
        // an unverifiable commit latches: the read-back is the old record while the write threw -> refused, not latched; a throwing write that changed memory is session usable
        val r = ZephyrPairSave.commitMove(d.store, verified(d, "192.168.1.77"))
        assertTrue(r is ZephyrMove.Commit.Written && !r.committed)   // new address in memory, commit not confirmed
        assertFalse(ZephyrPairSave.isLatched(d.store))
    }

    @Test fun anUnreadableStoreAfterAWriteLatchesAndBlocks() {
        val d = paired(); val v = verified(d, "192.168.1.77")
        d.failCommit = true
        d.onCredentialRead = { if (d.commits > 0) { d.cred = "garbage" } }
        assertTrue(ZephyrPairSave.commitMove(d.store, v) === ZephyrMove.Commit.Blocked)
        assertTrue(ZephyrPairSave.isLatched(d.store))
    }

    @Test fun c1aRecordThatDiffersFromTheSnapshotInAnythingButHostAndPortIsNeverAMove() {
        val d = paired(); val v = verified(d, "192.168.1.77")
        // after our write lands, something else replaces the record with another token at the same new address
        // updateAddress reads the record twice (before and after its write); the third read is commitMove's own re-read
        d.onCredentialRead = { if (d.reads == 3) d.cred = "z1|$pc|$pin|192.168.1.77|47823|$tok2" }
        assertTrue(ZephyrPairSave.commitMove(d.store, v) === ZephyrMove.Commit.Refused)
    }

    @Test fun c1aConcurrentRePairBetweenWriteAndReReadIsNotAMoveAndLeavesTheWinner() {
        val d = paired(); val v = verified(d, "192.168.1.77")
        var winner = ""
        d.onCredentialRead = { if (d.reads == 3) { winner = "z1|$pc2|$pin2|192.168.1.9|47823|$tok2"; d.cred = winner } }
        assertTrue(ZephyrPairSave.commitMove(d.store, v) === ZephyrMove.Commit.Refused)
        assertEquals(winner, d.cred)
    }

    @Test fun anUnchangedAddressWritesNothingAndRaisesNoNotice() {
        val d = paired(); val r = ZephyrPairSave.commitMove(d.store, verified(d, "192.168.1.5")) as ZephyrMove.Commit.Written
        assertFalse(r.committed); assertEquals(0, d.commits)
    }

    // ---- attempt (Amendment B, test 4) ----
    @Test fun savedEndpointAnsweringMeansNoDiscoveryAndNoMove() {
        val d = paired(); var discovered = false
        val out = ZephyrMove.attempt(port(d), pingSaved = { true }, discover = { discovered = true; emptyList() })
        assertTrue(out === ZephyrMove.Outcome.SavedEndpointSeen); assertFalse(discovered); assertEquals(0, d.commits)
    }

    @Test fun aDiscoveryListWithOnlyTheSavedAddressIsNoneVerifiedNeverSavedEndpointSeen() {
        val d = paired()
        val out = ZephyrMove.attempt(port(d), pingSaved = { false }, discover = { listOf(h("192.168.1.5")) },
            verify = { _, a, p -> fail("must not probe the saved endpoint $a:$p"); false })
        assertTrue(out is ZephyrMove.Outcome.NoneVerified); assertEquals(0, d.commits)
        assertNull((out as ZephyrMove.Outcome.NoneVerified).sameNameSeen)   // the saved address itself is not a "moved" label
    }

    @Test fun aSpoofedSavedAddressEntryDoesNotHideTheRealMovedPlayer() {
        val d = paired(); val probed = mutableListOf<String>()
        val out = ZephyrMove.attempt(port(d), pingSaved = { false }, discover = { listOf(h("192.168.1.5"), h("192.168.1.77")) },
            verify = { _, a, _ -> probed += a; true })
        out as ZephyrMove.Outcome.Moved
        assertEquals("192.168.1.77", out.credential.host); assertEquals(listOf("192.168.1.77"), probed); assertTrue(out.committed)
    }

    @Test fun anImpostorWithTheSameNameAndAnotherKeyWritesAndMarksNothing() {
        val d = paired(); val before = d.cred
        val out = ZephyrMove.attempt(port(d), pingSaved = { false }, discover = { listOf(h("192.168.1.66", name = "office pc")) }, verify = { _, _, _ -> false })
        out as ZephyrMove.Outcome.NoneVerified
        assertEquals("office pc", out.sameNameSeen)            // a label only: the hero says pair again
        assertEquals(before, d.cred); assertEquals(0, d.commits); assertFalse(KeyChangeState.isChanged(pc))
    }

    @Test fun aRenamedPlayerIsFoundByKeyAndRecordKeepsIdentityAndToken() {
        val d = paired()
        val out = ZephyrMove.attempt(port(d), pingSaved = { false }, discover = { listOf(h("192.168.1.77", name = "New name")) }, verify = { _, _, _ -> true })
        out as ZephyrMove.Outcome.Moved
        assertEquals(listOf(pc, pin, tok), listOf(out.credential.pcId, out.credential.pinHex, out.credential.token))
    }

    @Test fun nonPrivateSavedDuplicateAndOverThreeCandidatesAreIgnored() {
        val d = paired(); val probed = mutableListOf<String>()
        val found = listOf(h("8.8.8.8"), h("192.168.1.5"), h("192.168.1.10"), h("192.168.1.10"), h("192.168.1.11"), h("192.168.1.12"), h("192.168.1.13"), h("192.168.1.14", p = 0))
        val out = ZephyrMove.attempt(port(d), pingSaved = { false }, discover = { found }, verify = { _, a, _ -> probed += a; false })
        assertTrue(out is ZephyrMove.Outcome.NoneVerified)
        assertEquals(listOf("192.168.1.10", "192.168.1.11", "192.168.1.12"), probed)
    }

    @Test fun theHandshakeCheckSendsNoTokenAndTheTokenOnlyEverStaysInTheStore() {
        // The only callbacks are boolean handshakes on (credential, host, port): there is no parameter through which a token or request could reach a candidate.
        val d = paired(); val seen = mutableListOf<String>()
        ZephyrMove.attempt(port(d), pingSaved = { c -> seen += "ping ${c.host}"; false }, discover = { listOf(h("192.168.1.77")) }, verify = { _, a, _ -> seen += "verify $a"; true })
        assertEquals(listOf("ping 192.168.1.5", "verify 192.168.1.77"), seen)
    }

    @Test fun notPairedDoesNothing() {
        val d = Disk(); var any = false
        val out = ZephyrMove.attempt(port(d), pingSaved = { any = true; true }, discover = { any = true; emptyList() })
        assertTrue(out === ZephyrMove.Outcome.NotPaired); assertFalse(any)
    }

    // ---- B2: cancellation around the write ----
    @Test fun aCancelSeenAfterTheProbeAndBeforeTheStoreLeavesTheBytesUntouched() {
        val d = paired(); val before = d.cred; var cancelled = false
        try {
            ZephyrMove.attempt(port(d), pingSaved = { false }, discover = { listOf(h("192.168.1.77")) },
                verify = { _, _, _ -> cancelled = true; true }, ensureActive = { if (cancelled) throw CancellationException("cancelled during the probe") })
            fail("must propagate the cancellation")
        } catch (e: CancellationException) { }
        assertEquals(before, d.cred); assertEquals(0, d.commits); assertEquals("", d.intentV)
    }

    @Test fun aCancelDuringDiscoveryAlsoStopsBeforeAnyProbeOrWrite() {
        val d = paired(); val before = d.cred; var probes = 0; var gone = false
        try {
            ZephyrMove.attempt(port(d), pingSaved = { false }, discover = { gone = true; listOf(h("192.168.1.77")) },
                verify = { _, _, _ -> probes++; true }, ensureActive = { if (gone) throw CancellationException() })
            fail("must propagate")
        } catch (e: CancellationException) { }
        assertEquals(0, probes); assertEquals(before, d.cred)
    }

    @Test fun aCancelEnteringTheWriteLeavesWhollyOldOrWhollyNewNeverMixed() {
        val d = paired(); val old = d.cred
        val newRec = "z1|$pc|$pin|192.168.1.77|47823|$tok"
        var cancelNow = false
        // The cancel arrives only once the write has begun: the check before the store already passed, so the write completes atomically.
        val out = ZephyrMove.attempt(port(d) { cancelNow = true }, pingSaved = { false }, discover = { listOf(h("192.168.1.77")) }, verify = { _, _, _ -> true },
            ensureActive = { if (cancelNow) throw CancellationException() })
        assertTrue(cancelNow)
        assertTrue(d.cred == old || d.cred == newRec)
        assertEquals(newRec, d.cred); out as ZephyrMove.Outcome.Moved
        val c = secure(d); assertEquals(listOf(pc, pin, tok), listOf(c.pcId, c.pinHex, c.token))
        // The caller (RemoteState) applies Moved only from a live coroutine; a cancelled one never reaches onDurableMove (see the wiring test).
    }
}
