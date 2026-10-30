package com.zephyr.remote

import java.net.Socket
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** M1 arbitration: exactly one of finish/abandon releases the request, in any
 *  order and under a race. Pure JVM - the release callback stands in for the
 *  logical socket/count release, which is covered end-to-end by the
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

    @Test fun withCommitAfterAbandon_refused() {
        val releases = AtomicInteger()
        val s = state(releases)
        assertTrue(s.abandon())
        assertEquals(null, s.withCommit { true })
    }

    @Test fun withCommitAfterFinish_refused() {
        val releases = AtomicInteger()
        val s = state(releases)
        assertTrue(s.finish())
        assertEquals(null, s.withCommit { true })
    }

    /** Tab: the deadline release must NOT wait for an in-flight commit. A commit
     *  that wins the gate finishes its rename independently; abandonment still
     *  releases socket/count/locks immediately. */
    @Test fun commitWinsThenAbandon_abandonDoesNotWaitForRename() {
        val releases = AtomicInteger()
        val s = state(releases)
        val entered = java.util.concurrent.CountDownLatch(1)
        val proceed = java.util.concurrent.CountDownLatch(1)
        var commitResult: Boolean? = null
        val committer = kotlin.concurrent.thread {
            commitResult = s.withCommit { entered.countDown(); proceed.await(5, java.util.concurrent.TimeUnit.SECONDS); true }
        }
        assertTrue(entered.await(5, java.util.concurrent.TimeUnit.SECONDS))
        val t0 = System.nanoTime()
        assertTrue(s.abandon())
        val waitedMs = (System.nanoTime() - t0) / 1_000_000
        assertTrue("abandon released immediately, not after the rename (waited ${waitedMs}ms)", waitedMs < 1000)
        assertEquals(1, releases.get())
        proceed.countDown()
        committer.join(5000)
        assertEquals("the winning commit finished its rename", true, commitResult)
        assertEquals("gate closed afterwards", null, s.withCommit { true })
    }

    @Test fun racingCommitAndAbandon_exactlyOneGateWinner() {
        repeat(200) {
            val releases = AtomicInteger()
            val s = state(releases)
            val commits = AtomicInteger()
            val a = kotlin.concurrent.thread { s.abandon() }
            val b = kotlin.concurrent.thread { if (s.withCommit { true } != null) commits.incrementAndGet() }
            a.join(); b.join()
            // Either the commit won (rename done, then cleanup) or abandonment won
            // (commit refused) - never both, never neither-hang, cleanup always once.
            assertEquals(1, releases.get())
            assertTrue(commits.get() <= 1)
            assertEquals(null, s.withCommit { true })
        }
    }
}
