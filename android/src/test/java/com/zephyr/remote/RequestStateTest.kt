package com.zephyr.remote

import java.net.Socket
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** M1 arbitration: exactly one of finish/abandon releases the request, in any
 *  order and under a race. Pure JVM - the release callback stands in for the
 *  socket/count/permit release, which is covered end-to-end by the
 *  instrumented StreamServerAndroidTest abandonment tests. */
class RequestStateTest {

    private fun state(releases: AtomicInteger) =
        StreamServer.RequestState(Socket(), "127.0.0.1") { releases.incrementAndGet() }

    @Test fun finishThenAbandon_releasesOnce() {
        val releases = AtomicInteger()
        val s = state(releases)
        assertTrue(s.finish())
        assertFalse(s.abandon())
        assertEquals(1, releases.get())
        assertFalse(s.isActive)
    }

    @Test fun abandonThenFinish_releasesOnce() {
        val releases = AtomicInteger()
        val s = state(releases)
        assertTrue(s.abandon())
        assertFalse(s.finish())
        assertEquals(1, releases.get())
        assertFalse(s.isActive)
    }

    @Test fun repeatedAbandon_releasesOnce() {
        val releases = AtomicInteger()
        val s = state(releases)
        assertTrue(s.abandon())
        assertFalse(s.abandon())
        assertEquals(1, releases.get())
    }

    @Test fun racingAbandonAndFinish_releaseExactlyOnce() {
        repeat(200) {
            val releases = AtomicInteger()
            val s = state(releases)
            val a = kotlin.concurrent.thread { s.abandon() }
            val b = kotlin.concurrent.thread { s.finish() }
            a.join(); b.join()
            assertEquals("exactly one release under race", 1, releases.get())
            assertFalse(s.isActive)
        }
    }
}
