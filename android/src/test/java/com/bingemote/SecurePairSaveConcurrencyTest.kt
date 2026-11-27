package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.atomic.AtomicInteger

/** save, forget and updateAddress must not interleave their write-ahead intent windows on one store. */
class SecurePairSaveConcurrencyTest {
    private val pin = "ab".repeat(32)
    private val tok = "a1".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"

    /** In-memory store whose writes are slow and which records whether two operations ever held the intent marker at once. */
    private class SlowStore : SecurePairStore {
        @Volatile var cred = ""; @Volatile var intentV = ""; @Volatile var vlc = ""; @Volatile var name = ""; @Volatile var legacy = ""
        val overlaps = AtomicInteger()
        private fun nap() = Thread.sleep(2)
        override var credential: String get() = cred; set(v) { nap(); cred = v }
        override var intent: String
            get() = intentV
            set(v) { if (v.isNotEmpty() && intentV.isNotEmpty()) overlaps.incrementAndGet(); nap(); intentV = v }
        override var vlcPassword: String get() = vlc; set(v) { nap(); vlc = v }
        override var pcName: String get() = name; set(v) { nap(); name = v }
        override var legacyToken: String get() = legacy; set(v) { nap(); legacy = v }
        override fun flush() = true
        override fun healthy() = true
    }

    private fun save(s: SecurePairStore, host: String) =
        SecurePairSave.save(s, host, 8492, "PC", "pw", SecureHttp.BoundPin(pin, tok), pc)

    @Test fun overlappingSaveUpdateAndForgetNeverShareTheIntentWindow() {
        val s = SlowStore()
        assertEquals(SecurePairSave.Outcome.SAVED, save(s, "192.168.1.5"))
        val workers = 6; val rounds = 15
        val go = CountDownLatch(1); val done = CountDownLatch(workers)
        for (w in 0 until workers) Thread {
            go.await()
            repeat(rounds) { i ->
                when ((w + i) % 3) {
                    0 -> save(s, "192.168.1.${10 + w}")
                    1 -> SecurePairSave.updateAddress(s, s.cred, "192.168.2.${w + 1}", 8492)
                    else -> if (i % 5 == 0) SecurePairSave.forget(s) else save(s, "192.168.3.${w + 1}")
                }
            }
            done.countDown()
        }.apply { isDaemon = true; start() }
        go.countDown(); assertTrue("workers did not finish", done.await(60, java.util.concurrent.TimeUnit.SECONDS))
        assertEquals("two operations held the intent marker at once", 0, s.overlaps.get())
        assertEquals("", s.intentV)                                   // every operation cleared its marker
        assertFalse(SecurePairSave.isLatched(s))
        // The end state is either wholly unpaired or one coherent record, never a mix.
        val c = s.cred
        assertTrue(c.isEmpty() || PairedCredential.decode(c) != null)
    }
}
