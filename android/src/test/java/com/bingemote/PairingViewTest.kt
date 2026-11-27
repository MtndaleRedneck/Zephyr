package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class PairingViewTest {
    private val pin = "ab".repeat(32)
    private val pc = "0123456789abcdef0123456789abcdef"
    private fun tok(n: Int) = "%064x".format(n)

    private class Store : SecurePairStore {
        @Volatile var cred = ""; @Volatile var intentV = ""; @Volatile var vlc = ""; @Volatile var name = ""; @Volatile var legacy = ""
        override var credential: String get() = cred; set(v) { Thread.sleep(1); cred = v }
        override var intent: String get() = intentV; set(v) { intentV = v }
        override var vlcPassword: String get() = vlc; set(v) { vlc = v }
        override var pcName: String get() = name; set(v) { name = v }
        override var legacyToken: String get() = legacy; set(v) { legacy = v }
        override fun flush() = true
        override fun healthy() = true
    }
    private fun save(s: SecurePairStore, host: String, token: String) =
        SecurePairSave.save(s, host, 8492, "PC", "pw", SecureHttp.BoundPin(pin, token), pc)
    private fun viewOf(s: SecurePairStore) = PairingView { (SecurePairSave.load(s) as? PairedCredential.State.Secure)?.credential }

    @Test fun aDelayedOlderTriggerCannotPublishAnOlderRecord() {
        val s = Store(); val v = viewOf(s)
        save(s, "192.168.1.5", tok(1))                  // commit A (its trigger is delayed)
        save(s, "192.168.1.9", tok(2))                  // commit B
        v.publishCurrent()                              // trigger B delivered first
        v.publishCurrent()                              // delayed trigger A delivered last: it re-reads, so it cannot go back
        assertEquals("192.168.1.9", v.snapshot!!.host); assertEquals(tok(2), v.snapshot!!.token)
    }

    @Test fun forgetClearsTheView() {
        val s = Store(); val v = viewOf(s)
        save(s, "192.168.1.5", tok(1)); v.publishCurrent()
        SecurePairSave.forget(s); v.publishCurrent()
        assertNull(v.snapshot)
    }

    @Test fun concurrentCommitsAndTriggersConvergeToTheStoredRecord() {
        repeat(20) { round ->
            val s = Store(); val v = viewOf(s)
            save(s, "192.168.1.1", tok(0)); v.publishCurrent()
            val workers = 6; val go = CountDownLatch(1); val done = CountDownLatch(workers)
            for (w in 0 until workers) Thread {
                go.await()
                repeat(10) { i ->
                    val n = w * 100 + i + 1
                    val ok = when ((w + i + round) % 3) {
                        0 -> save(s, "192.168.7.${n % 200 + 1}", tok(n)) == SecurePairSave.Outcome.SAVED
                        1 -> SecurePairSave.updateAddress(s, s.cred, "192.168.8.${n % 200 + 1}", 8492).usable
                        else -> SecurePairSave.forget(s)
                    }
                    if (ok) v.publishCurrent()          // every successful commit fires its own trigger, possibly delivered late
                }
                done.countDown()
            }.apply { isDaemon = true; start() }
            go.countDown(); assertTrue("workers stuck", done.await(60, TimeUnit.SECONDS))
            // Once every commit has delivered its trigger, the view equals the store, whatever the interleaving was.
            val stored = (SecurePairSave.load(s) as? PairedCredential.State.Secure)?.credential
            val snap = v.snapshot
            if (stored == null) assertNull("round $round", snap)
            else { assertEquals("round $round", listOf(stored.host, stored.port, stored.token), listOf(snap!!.host, snap.port, snap.token)) }
        }
    }
}
