package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * Address move: ONE commit of ONE record, no marker. The store model keeps a memory view and a "disk" view apart, so a refused
 * commit can leave memory ahead of disk. What a reopened phone shows after such a failure is unproven and the tests say so:
 * they only require a coherent record with the same identity, pin and token, never which address.
 */
class SecurePairMoveTest {
    private val pin = "ab".repeat(32)
    private val tok = "a1".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"

    private enum class Mode { OK, THROW_BEFORE_MEMORY, THROW_AFTER_MEMORY, GARBAGE_AFTER, POISON_AFTER, OTHER_IDENTITY_AFTER, SILENT_LOST_WRITE }

    private class MoveStore(var memory: String = "", var disk: String = "") : SecurePairStore {
        var mode = Mode.OK
        var commits = 0; var intentWrites = 0; var flushes = 0
        @Volatile var poisoned = false
        var intentV = ""; var vlc = "vlc"; var name = "Office PC"; var legacy = ""
        override var credential: String
            get() { if (poisoned) throw SecureStoreUnavailable(); return memory }
            set(v) {
                commits++
                when (mode) {
                    Mode.OK -> { memory = v; disk = v }
                    Mode.THROW_BEFORE_MEMORY -> throw IllegalStateException("refused")
                    Mode.THROW_AFTER_MEMORY -> { memory = v; throw IllegalStateException("commit refused") }
                    Mode.GARBAGE_AFTER -> { memory = "v3|garbage"; throw IllegalStateException("commit refused") }
                    Mode.POISON_AFTER -> { memory = v; poisoned = true; throw IllegalStateException("commit refused") }
                    Mode.OTHER_IDENTITY_AFTER -> { memory = PairedCredential.of("f".repeat(32), pinOf(), "10.9.9.9", 9, tokOf())!!.encode(); throw IllegalStateException("commit refused") }
                    Mode.SILENT_LOST_WRITE -> { /* reports success, store still shows the old record */ }
                }
            }
        private fun pinOf() = "cd".repeat(32); private fun tokOf() = "e5".repeat(32)
        override var intent: String get() = intentV; set(v) { intentWrites++; intentV = v }
        override var vlcPassword: String get() = vlc; set(v) { vlc = v }
        override var pcName: String get() = name; set(v) { name = v }
        override var legacyToken: String get() = legacy; set(v) { legacy = v }
        override fun flush(): Boolean { flushes++; return true }
        override fun healthy() = !poisoned
        /** A fresh process: it sees only the disk view. */
        fun reopen() = MoveStore(disk, disk)
    }

    @Before fun reset() { KeyChangeState.resetForTests() }

    private fun paired(host: String = "192.168.1.5"): Pair<MoveStore, String> {
        val s = MoveStore()
        assertEquals(SecurePairSave.Outcome.SAVED, SecurePairSave.save(s, host, 8492, "Office PC", "pw", SecureHttp.BoundPin(pin, tok), pc))
        val rec = s.memory; s.commits = 0; s.intentWrites = 0; s.flushes = 0
        s.disk = rec
        return s to rec
    }
    private fun cred(s: SecurePairStore) = (SecurePairSave.load(s) as PairedCredential.State.Secure).credential
    private fun sameIdentity(c: PairedCredential) = assertEquals(listOf(pc, pin, tok), listOf(c.pcId, c.pinHex, c.token))

    @Test fun successIsOneCommitNoMarkerNoFlushAndKeepsIdentity() {
        val (s, rec) = paired()
        assertEquals(SecurePairSave.MoveResult.COMMITTED, SecurePairSave.updateAddress(s, rec, "192.168.1.9", 9000))
        assertEquals(1, s.commits); assertEquals(0, s.intentWrites); assertEquals(0, s.flushes)
        val c = cred(s); sameIdentity(c); assertEquals("192.168.1.9", c.host); assertEquals(9000, c.port)
        assertEquals("", s.intentV)
        val r = cred(s.reopen()); assertEquals("192.168.1.9", r.host)         // the commit reported success and the disk model holds it
    }

    @Test fun sameAddressWritesNothing() {
        val (s, rec) = paired()
        assertEquals(SecurePairSave.MoveResult.UNCHANGED, SecurePairSave.updateAddress(s, rec, "192.168.1.5", 8492))
        assertEquals(0, s.commits)
    }

    @Test fun staleSnapshotKeyChangeAndBusyMarkerWriteNothing() {
        val (s, rec) = paired()
        assertEquals(SecurePairSave.MoveResult.REFUSED, SecurePairSave.updateAddress(s, rec.replace("192.168.1.5", "192.168.1.6"), "192.168.1.9", 9000))
        s.intentV = "saving"
        assertEquals(SecurePairSave.MoveResult.REFUSED, SecurePairSave.updateAddress(s, rec, "192.168.1.9", 9000))
        s.intentV = ""
        KeyChangeState.mark(pc)
        assertEquals(SecurePairSave.MoveResult.REFUSED, SecurePairSave.updateAddress(s, rec, "192.168.1.9", 9000))
        assertTrue(KeyChangeState.isChanged(pc))
        assertEquals(0, s.commits); assertFalse(SecurePairSave.isLatched(s))
    }

    @Test fun unhealthyOrUnreadableBeforeTheWriteWritesNothingAndDoesNotLatch() {
        val (s, rec) = paired(); s.poisoned = true
        assertEquals(SecurePairSave.MoveResult.REFUSED, SecurePairSave.updateAddress(s, rec, "192.168.1.9", 9000))
        assertEquals(0, s.commits); assertFalse(SecurePairSave.isLatched(s))
    }

    @Test fun refusedCommitWithNothingInMemoryIsRefusedNotLatched() {
        val (s, rec) = paired(); s.mode = Mode.THROW_BEFORE_MEMORY
        assertEquals(SecurePairSave.MoveResult.REFUSED, SecurePairSave.updateAddress(s, rec, "192.168.1.9", 9000))
        assertFalse(SecurePairSave.isLatched(s)); assertEquals("192.168.1.5", cred(s).host)
        assertEquals("192.168.1.5", cred(s.reopen()).host)
    }

    @Test fun refusedCommitWithMemoryAheadIsSessionUsableNotLatchedNotDurable() {
        val (s, rec) = paired(); s.mode = Mode.THROW_AFTER_MEMORY
        assertEquals(SecurePairSave.MoveResult.SESSION_USABLE, SecurePairSave.updateAddress(s, rec, "192.168.1.9", 9000))
        assertFalse(SecurePairSave.isLatched(s))
        val c = cred(s); sameIdentity(c); assertEquals("192.168.1.9", c.host)        // this process sees the new address
        // A reopened process is NOT proven to see it. In this model the disk kept the old record; the test only requires a coherent same-identity record.
        val r = cred(s.reopen()); sameIdentity(r); assertTrue(r.host == "192.168.1.5" || r.host == "192.168.1.9")
        assertEquals(1, s.commits); assertEquals(0, s.intentWrites)
    }

    @Test fun unverifiableStatesAfterAFailureBlockAndLatch() {
        for (m in listOf(Mode.GARBAGE_AFTER, Mode.POISON_AFTER, Mode.OTHER_IDENTITY_AFTER, Mode.SILENT_LOST_WRITE)) {
            val (s, rec) = paired(); s.mode = m
            assertEquals("mode $m", SecurePairSave.MoveResult.BLOCKED, SecurePairSave.updateAddress(s, rec, "192.168.1.9", 9000))
            assertTrue("mode $m", SecurePairSave.isLatched(s))
            assertTrue("mode $m", SecurePairSave.load(s) is PairedCredential.State.Blocked)
        }
    }

    @Test fun saveAndForgetKeepTheirMarkerAndFlush() {
        val s = MoveStore()
        SecurePairSave.save(s, "192.168.1.5", 8492, "PC", "pw", SecureHttp.BoundPin(pin, tok), pc)
        assertTrue("save writes marker twice", s.intentWrites >= 2); assertTrue(s.flushes > 0)
        val w = s.intentWrites
        assertTrue(SecurePairSave.forget(s))
        assertTrue(s.intentWrites >= w + 2); assertEquals("", s.memory)
    }
}
