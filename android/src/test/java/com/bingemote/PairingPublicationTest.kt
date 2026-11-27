package com.bingemote

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** The published helper/VLC tuple: outer-section publication, poison normalization, legacy fallback equality, reader vs writer. Pure JVM. */
class PairingPublicationTest {
    @Before @After fun reset() = PairingPublication.resetForTests()

    private fun tuple(token: String, host: String = "h", pw: String = "pw") = Store.HelperVlcSnapshot(Store.HelperEndpoint(host, 8492, token), 8080, pw)

    private class Src(var next: () -> Store.HelperVlcSnapshot) : PairingPublicationSource {
        var broken = false; var loads = 0
        override fun loadTuple(): Store.HelperVlcSnapshot { loads++; return next() }
        override fun isBroken() = broken
    }
    private class Fake(val src: PairingPublicationSource?) : SecurePairStore {
        override var credential = ""; override var intent = ""; override var vlcPassword = ""; override var pcName = ""; override var legacyToken = ""
        override fun flush() = true
        override fun healthy() = true
        override val publication: PairingPublicationSource? get() = src
    }

    @Test fun outerSectionPublishesOnceAfterAThrowingInnerWrite() {
        var stored = tuple("old"); val src = Src { stored }; val f = Fake(src)
        PairingPublication.readAndPublish(src)
        assertEquals("old", PairingPublication.peek()?.endpoint?.token)
        val loadsBefore = src.loads
        try {
            SecurePairSave.writerSection(f) {
                assertNull(PairingPublication.peek())                       // unpublished while the writer is in flight
                SecurePairSave.writerSection(f) { stored = tuple("new") }   // inner: no publish
                assertNull(PairingPublication.peek())
                SecurePairSave.writerSection(f) { throw IllegalStateException("inner write failed") }
            }
            throw AssertionError("must throw")
        } catch (e: IllegalStateException) { }
        assertEquals(1, src.loads - loadsBefore)                             // exactly one load, at the outermost exit
        assertEquals("new", PairingPublication.peek()?.endpoint?.token)     // the whole stored tuple after the failure, not the old one
    }

    @Test fun poisonedLoadIsReloadedAndTheMixedTupleIsNeverPublishedOrReturned() {
        val mixed = tuple("tok", pw = "real"); val blank = tuple("", pw = "")
        var calls = 0
        val src = Src { calls++; if (calls == 1) { mixed } else blank }
        src.next = { if (calls++ == 0) { src.broken = true; mixed } else blank }
        calls = 0
        val got = PairingPublication.readAndPublish(src)
        assertEquals("", got.endpoint.token); assertEquals("", got.vlcPassword)
        assertEquals("", PairingPublication.peek()?.vlcPassword)
        assertEquals(2, src.loads)
        // same rule at a writer section exit
        PairingPublication.resetForTests(); calls = 0; src.loads = 0; src.broken = false
        SecurePairSave.writerSection(Fake(src)) { }
        assertEquals("", PairingPublication.peek()?.vlcPassword)
        assertEquals(2, src.loads)
    }

    @Test fun legacyFallbackTupleIsEqualAcrossReads() {
        val legacy = { Store.HelperVlcSnapshot(Store.HelperEndpoint("pc.local", 8492, "legacy-secretbox-token"), 8080, "vlcpw") }
        val src = Src(legacy)
        val a = PairingPublication.readAndPublish(src); val b = PairingPublication.readAndPublish(src)
        assertTrue(a.sameTarget(b)); assertTrue(a.sameTarget(PairingPublication.peek()))
        val unpaired = Src { Store.HelperVlcSnapshot(Store.HelperEndpoint("", 8492, ""), 8080, "") }
        PairingPublication.readAndPublish(unpaired)
        assertNotNull(PairingPublication.peek())                              // unpaired publishes its blank tuple, not null
    }

    @Test fun aReaderNeverPublishesOverAnInFlightWriter() {
        var stored = tuple("old"); val src = Src { stored }; val f = Fake(src)
        // same thread, inside the section (reentrant lock): returns a tuple but publishes nothing
        SecurePairSave.writerSection(f) {
            stored = tuple("mid")
            assertEquals("mid", PairingPublication.readAndPublish(src).endpoint.token)
            assertNull(PairingPublication.peek())
            stored = tuple("final")
        }
        assertEquals("final", PairingPublication.peek()?.endpoint?.token)
        // another thread: blocked on the write lock until the section ends, then it reads the final value
        val inSection = CountDownLatch(1); val release = CountDownLatch(1); val readerDone = CountDownLatch(1)
        val w = Thread { SecurePairSave.writerSection(f) { stored = tuple("w2"); inSection.countDown(); release.await(5, TimeUnit.SECONDS) } }
        w.start(); assertTrue(inSection.await(5, TimeUnit.SECONDS))
        // the reader goes through SecurePairSave.readAndPublish (what Store.helperVlcSnapshot calls): it needs the write lock
        val r2 = Thread { SecurePairSave.readAndPublish(src); readerDone.countDown() }
        r2.start()
        // deterministic: wait (bounded) until the reader is parked on the write lock, then it cannot have completed or published
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (r2.state != Thread.State.BLOCKED && System.nanoTime() < deadline) Thread.yield()
        assertEquals(Thread.State.BLOCKED, r2.state)
        assertEquals(1L, readerDone.count)
        assertNull(PairingPublication.peek())
        release.countDown(); w.join(5000); assertTrue(readerDone.await(5, TimeUnit.SECONDS))
        assertEquals("w2", PairingPublication.peek()?.endpoint?.token)
    }

    @Test fun aDoubleWithNoStoreBehindItPublishesNothing() {
        PairingPublication.readAndPublish(Src { tuple("x") })
        SecurePairSave.writerSection(Fake(null)) { }
        assertEquals("x", PairingPublication.peek()?.endpoint?.token)
    }

    @Test fun aPoisonEventDropsThePublishedTupleAtOnceAndDoesNoStorageWork() {
        val src = Src { tuple("tok") }
        PairingPublication.readAndPublish(src)
        assertNotNull(PairingPublication.peek())
        val loads = src.loads
        PairingPublication.invalidate()                      // what the secret box calls on its first failed read
        assertNull(PairingPublication.peek())
        assertEquals(loads, src.loads)                       // nothing loaded by the callback
    }

    @Test fun aLoadThatRacesAPoisonIsNotPublishedAndIsReloadedBlank() {
        val good = tuple("tok", pw = "real"); val blank = tuple("", pw = "")
        val src = object : PairingPublicationSource {
            var broken = false; var loads = 0; var checks = 0
            override fun loadTuple(): Store.HelperVlcSnapshot { loads++; return if (broken) blank else good }
            override fun isBroken(): Boolean {
                // the poison lands right after the first health check said "fine": the stale good tuple must not be published
                if (checks++ == 0) { val before = broken; broken = true; PairingPublication.invalidate(); return before }
                return broken
            }
        }
        val got = PairingPublication.readAndPublish(src)
        assertEquals("", got.endpoint.token)
        assertEquals("", PairingPublication.peek()?.endpoint?.token)
        assertEquals("", PairingPublication.peek()?.vlcPassword)
    }

    private fun snap(host: String = "h1", port: Int = 8492, tok: String = "t", vp: Int = 8080, pw: String = "pw", secure: Boolean) =
        Store.HelperVlcSnapshot(Store.HelperEndpoint(host, port, tok), vp, pw, secure)

    @Test fun secureSamePcKeepsAVerifiedMoveAndDetectsTokenPasswordPortChanges() {
        val a = snap(secure = true)
        assertTrue(a.samePc(snap(host = "h2", port = 9999, secure = true)))      // verified address move: same PC
        assertFalse(a.samePc(snap(pw = "pw2", secure = true)))                    // same token, changed VLC password
        assertFalse(a.samePc(snap(vp = 9090, secure = true)))                     // changed VLC port
        assertFalse(a.samePc(snap(tok = "t2", secure = true)))                    // re-pair
        assertFalse(a.samePc(null))
    }

    @Test fun legacyPlainSamePcComparesTheWholeTargetIncludingHostAndHelperPort() {
        val a = snap(secure = false)
        assertTrue(a.samePc(snap(secure = false)))
        assertFalse(a.samePc(snap(host = "h2", secure = false)))                  // plain host edit under the same token drops
        assertFalse(a.samePc(snap(port = 9999, secure = false)))                  // plain helper port change drops
        assertFalse(a.samePc(snap(pw = "pw2", secure = false)))
    }

    @Test fun aSecurePlainMismatchIsNeverTheSamePc() {
        assertFalse(snap(secure = true).samePc(snap(secure = false)))
        assertFalse(snap(secure = false).samePc(snap(secure = true)))
    }
}
