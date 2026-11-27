package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Zephyr save protocol on in-memory fakes with injected write failures. JVM logic only: no Keystore, no device. */
class ZephyrPairSaveTest {
    private val pin = "ab".repeat(32); private val pin2 = "cd".repeat(32)
    private val tok = "a1".repeat(32); private val tok2 = "b2".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"; private val pc2 = "f".repeat(32)

    private class Disk(var crashAfter: Int = Int.MAX_VALUE, var readFails: Boolean = false, var legacy: Boolean = false) {
        val m = hashMapOf("cred" to "", "intent" to "", "name" to "old")
        var writes = 0
        fun put(k: String, v: String) { writes++; if (writes <= crashAfter) m[k] = v }
        fun get(k: String): String { if (readFails) throw SecureStoreUnavailable(); return m[k]!! }
        val store = object : ZephyrPairStore {
            override var credential: String get() = get("cred"); set(v) = put("cred", v)
            override var intent: String get() = get("intent"); set(v) = put("intent", v)
            override var pcName: String get() = get("name"); set(v) = put("name", v)
            override fun flush() = true
            override fun healthy() = !readFails
        }
        /** What a restarted phone would see: a new store over the persisted values. */
        fun freshLoad(): ZephyrCredential.State {
            val d = Disk(readFails = readFails, legacy = legacy); d.m.putAll(m)
            return ZephyrPairSave.load(d.store) { d.legacy }
        }
    }

    private fun save(d: Disk, token: String = tok, host: String = "192.168.1.5", pcId: String = pc, pinHex: String = pin) =
        ZephyrPairSave.save(d.store, host, 47823, "PC", SecureHttp.BoundPin(pinHex, token), pcId)

    private fun secure(s: ZephyrCredential.State) = (s as ZephyrCredential.State.Secure).credential
    private fun totalWrites(): Int { val d = Disk(); save(d); return d.writes }

    @Test fun cleanSaveLoadsSecureWithIdentityPinAddressAndTokenTogether() {
        val d = Disk(); assertEquals(SecurePairSave.Outcome.SAVED, save(d))
        val c = secure(d.freshLoad())
        assertEquals(listOf(pc, pin, "192.168.1.5", 47823, tok), listOf(c.pcId, c.pinHex, c.host, c.port, c.token))
        assertEquals("", d.m["intent"])
        assertTrue(d.m["cred"]!!.startsWith("z1|"))
    }

    @Test fun aZephyrRecordNeverParsesAsTheHelpersAndTheReverse() {
        val z = ZephyrCredential.of(pc, pin, "h", 1, tok)!!.encode()
        val h = PairedCredential.of(pc, pin, "h", 1, tok)!!.encode()
        assertNull(PairedCredential.decode(z)); assertNull(ZephyrCredential.decode(h))
        assertEquals(tok, ZephyrCredential.decode(z)!!.token)
        assertNull(ZephyrCredential.decode("z1|$pc|$pin|h|1|short"))
        assertNull(ZephyrCredential.decode("z1|$pc|$pin|h|0|$tok"))
        assertNull(ZephyrCredential.decode("z1|$pc|${pin.uppercase()}|h|1|$tok"))
    }

    @Test fun crashAtEveryWriteFromUnpairedIsUnpairedBlockedOrWholeNeverPartial() {
        val total = totalWrites()
        for (n in 0..total) {
            val d = Disk(crashAfter = n); save(d)
            val s = d.freshLoad()
            when {
                n == 0 -> assertTrue("n=$n", s === ZephyrCredential.State.Unpaired)
                n < total -> assertTrue("n=$n", s === ZephyrCredential.State.Blocked)
                else -> assertEquals(tok, secure(s).token)
            }
        }
    }

    @Test fun crashAtEveryWriteOverASecurePairingNeverExposesAMixedState() {
        val total = totalWrites()
        for (n in 0..total) {
            val d = Disk(); save(d, tok, "10.0.0.1", pc, pin)
            d.crashAfter = d.writes + n
            save(d, tok2, "10.0.0.2", pc2, pin2)
            val s = d.freshLoad()
            if (s is ZephyrCredential.State.Secure) {
                val c = s.credential
                val t = listOf(c.pcId, c.pinHex, c.host, c.token)
                assertTrue("n=$n $t", t == listOf(pc, pin, "10.0.0.1", tok) || t == listOf(pc2, pin2, "10.0.0.2", tok2))
            } else assertTrue("n=$n", s === ZephyrCredential.State.Blocked)
        }
    }

    @Test fun legacyOnlyIsNeedsRepairAndAFailedSaveNeverTouchesItOrFallsBackToIt() {
        val d = Disk(legacy = true)
        assertTrue(d.freshLoad() === ZephyrCredential.State.NeedsRepair)
        val total = totalWrites()
        for (n in 0 until total) {
            val e = Disk(crashAfter = n, legacy = true); save(e)
            val s = e.freshLoad()
            assertTrue("n=$n", s === ZephyrCredential.State.NeedsRepair || s === ZephyrCredential.State.Blocked)
            assertTrue("legacy flag untouched", e.legacy)
        }
        assertEquals(SecurePairSave.Outcome.SAVED, save(d)); assertEquals(tok, secure(d.freshLoad()).token)
    }

    @Test fun aStaleLegacyPairingNeverOverridesASecureRecord() {
        val d = Disk(legacy = true); save(d)
        assertEquals(tok, secure(d.freshLoad()).token)
    }

    @Test fun anUnreadableStoreIsBlockedNeverUnpaired() {
        val d = Disk(); save(d); d.readFails = true
        assertTrue(d.freshLoad() === ZephyrCredential.State.Blocked)
    }

    @Test fun anInvalidProofIsRefusedWithNothingWritten() {
        val d = Disk()
        assertEquals(SecurePairSave.Outcome.FAILED_PRIOR_KEPT, save(d, token = "short"))
        assertEquals(0, d.writes)
        assertTrue(d.freshLoad() === ZephyrCredential.State.Unpaired)
    }

    @Test fun aBlockedZephyrSaveLatchesOnlyThatStoreAndLeavesTheHelperAlone() {
        val d = Disk(crashAfter = 3)
        assertEquals(SecurePairSave.Outcome.FAILED_BLOCKED, save(d))
        assertTrue(ZephyrPairSave.isLatched(d.store))
        assertTrue(ZephyrPairSave.load(d.store) { false } === ZephyrCredential.State.Blocked)
        // A separate helper store saves normally and is not latched by it, and the reverse.
        val h = HelperFake()
        assertEquals(SecurePairSave.Outcome.SAVED, SecurePairSave.save(h, "10.0.0.9", 8492, "PC", "pw", SecureHttp.BoundPin(pin, tok), pc))
        assertFalse(SecurePairSave.isLatched(h))
        val z = Disk()
        val hb = HelperFake(failCred = true)
        assertEquals(SecurePairSave.Outcome.FAILED_BLOCKED, SecurePairSave.save(hb, "10.0.0.9", 8492, "PC", "pw", SecureHttp.BoundPin(pin, tok), pc))
        assertTrue(SecurePairSave.isLatched(hb))
        assertFalse(ZephyrPairSave.isLatched(z.store))
        assertEquals(SecurePairSave.Outcome.SAVED, save(z))
    }

    private class HelperFake(val failCred: Boolean = false) : SecurePairStore {
        val m = hashMapOf("cred" to "", "intent" to "", "vlc" to "", "name" to "", "legacy" to "")
        private var n = 0
        override var credential: String get() = m["cred"]!!; set(v) { n++; if (failCred && n >= 1 && v.startsWith("v3")) throw IllegalStateException("refused"); m["cred"] = v }
        override var intent: String get() = m["intent"]!!; set(v) { if (failCred && v.isEmpty()) throw IllegalStateException("refused"); m["intent"] = v }
        override var vlcPassword: String get() = m["vlc"]!!; set(v) { m["vlc"] = v }
        override var pcName: String get() = m["name"]!!; set(v) { m["name"] = v }
        override var legacyToken: String get() = m["legacy"]!!; set(v) { m["legacy"] = v }
        override fun flush() = true
        override fun healthy() = true
    }
}
