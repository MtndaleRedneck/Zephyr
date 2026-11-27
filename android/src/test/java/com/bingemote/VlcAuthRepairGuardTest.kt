package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class VlcAuthRepairGuardTest {
    private val pin = "ab".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"
    private fun tok(n: Int) = "%064x".format(n)

    private class Store : SecurePairStore {
        @Volatile var cred = ""; @Volatile var intentV = ""; @Volatile var vlc = ""; @Volatile var name = ""; @Volatile var legacy = ""
        override var credential: String get() = cred; set(v) { cred = v }
        override var intent: String get() = intentV; set(v) { intentV = v }
        override var vlcPassword: String get() = vlc; set(v) { vlc = v }
        override var pcName: String get() = name; set(v) { name = v }
        override var legacyToken: String get() = legacy; set(v) { legacy = v }
        override fun flush() = true
        override fun healthy() = true
    }
    private fun save(s: SecurePairStore, host: String, token: String, vlc: String) =
        SecurePairSave.save(s, host, 8492, "PC", vlc, SecureHttp.BoundPin(pin, token), pc)

    private var req: String? = null
    private val get = { req }
    private val set = { v: String? -> req = v }

    @Test fun samePcWritesTheAdoptedPassword() {
        val s = Store(); save(s, "192.168.1.5", tok(1), "old"); req = "r1"
        assertTrue(VlcAuthRepair.storeAdopted(s, tok(1), "adopted", "r1", get, set))
        assertEquals("adopted", s.vlcPassword); assertEquals("r1", req)
    }

    @Test fun aMovedAddressWithTheSameTokenStillWrites() {
        val s = Store(); save(s, "192.168.1.5", tok(1), "old"); req = "r1"
        SecurePairSave.updateAddress(s, s.credential, "192.168.1.9", 8492)
        assertTrue(VlcAuthRepair.storeAdopted(s, tok(1), "adopted", "r1", get, set))
        assertEquals("adopted", s.vlcPassword)
    }

    @Test fun rePairBeforeTheWriteKeepsTheNewPcPasswordAndClearsOnlyOurOwnId() {
        val s = Store(); save(s, "192.168.1.5", tok(1), "old"); req = "r1"
        save(s, "192.168.1.7", tok(2), "newpc")                  // a re-pair landed while the adopt fetch was out
        assertFalse(VlcAuthRepair.storeAdopted(s, tok(1), "pc-a-password", "r1", get, set))
        assertEquals("newpc", s.vlcPassword); assertNull(req)    // our superseded id is dropped
    }

    @Test fun rePairBeforeTheWriteNeverClearsADifferentRequestId() {
        val s = Store(); save(s, "192.168.1.5", tok(1), "old"); req = "newer-op"
        save(s, "192.168.1.7", tok(2), "newpc")
        assertFalse(VlcAuthRepair.storeAdopted(s, tok(1), "pc-a-password", "r1", get, set))
        assertEquals("newpc", s.vlcPassword); assertEquals("newer-op", req)
    }

    @Test fun ackFailureReplayKeepsTheIdAndAckSuccessClearsOnlyOurOwn() {
        val s = Store(); save(s, "192.168.1.5", tok(1), "old"); req = "r1"
        assertTrue(VlcAuthRepair.storeAdopted(s, tok(1), "adopted", "r1", get, set))
        // ack failed: the caller does not clear; the id is still there to replay
        assertEquals("r1", req)
        VlcAuthRepair.clearOwnRequest(s, tok(1), "r1", get, set); assertNull(req)           // later successful ack
        req = "other"; VlcAuthRepair.clearOwnRequest(s, tok(1), "r1", get, set); assertEquals("other", req)
    }

    @Test fun aStoredIdIsReusedOnlyForTheSamePcAndOtherPairingsGetAFreshOne() {
        val tag1 = VlcAuthRepair.tag(tok(1), "abc")
        assertEquals(Pair(false, "abc"), VlcAuthRepair.requestSlot(tok(1), tag1))          // crash/lost-response replay keeps the id
        val other = VlcAuthRepair.requestSlot(tok(2), tag1)                                // re-paired to another PC
        assertTrue(other.first); assertTrue(other.second != "abc")
        assertTrue(VlcAuthRepair.requestSlot(tok(1), "legacy-untagged").first)             // an untagged leftover is not replayed
        assertTrue(VlcAuthRepair.requestSlot(tok(1), null).first)
    }

    @Test fun mintWritesNothingWhenThePairingIsNotTheCapturedPc() {
        val s = Store(); save(s, "192.168.1.5", tok(1), "old"); req = null
        save(s, "192.168.1.7", tok(2), "newpc")                                  // re-paired before the mint
        assertFalse(VlcAuthRepair.mintSlot(s, tok(1), "slot", set)); assertNull(req)
        assertTrue(VlcAuthRepair.mintSlot(s, tok(2), "slot2", set)); assertEquals("slot2", req)
    }

    @Test fun unpairedStoreWritesNothing() {
        val s = Store(); req = "r1"
        assertFalse(VlcAuthRepair.storeAdopted(s, tok(1), "pw", "r1", get, set))
        assertEquals("", s.vlcPassword)
    }
}
