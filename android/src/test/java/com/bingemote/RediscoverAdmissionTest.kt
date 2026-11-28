package com.bingemote

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.atomic.AtomicInteger

class RediscoverAdmissionTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    @Test fun manyCallersAtOnceAdmitExactlyOne() {
        repeat(20) {
            val a = RediscoverAdmission(clock = { 1_000_000L })
            val admitted = AtomicInteger(); val go = CountDownLatch(1); val done = CountDownLatch(16)
            repeat(16) { Thread { go.await(); if (a.tryAdmit()) admitted.incrementAndGet(); done.countDown() }.apply { isDaemon = true; start() } }
            go.countDown(); assertTrue(done.await(10, java.util.concurrent.TimeUnit.SECONDS))
            assertEquals(1, admitted.get())
        }
    }

    @Test fun firstCallIsAdmittedEvenAtClockZero() {
        assertTrue(RediscoverAdmission(clock = { 0L }).tryAdmit())
    }

    @Test fun aJobCancelledBeforeItsBodyStartsStillReleasesSoNothingWedges() {
        var now = 1_000L
        val a = RediscoverAdmission(clock = { now })
        val ran = AtomicInteger()
        // A one-thread dispatcher held busy, so the admitted job is queued and its body has not started when we cancel it.
        val ex = java.util.concurrent.Executors.newSingleThreadExecutor { r -> Thread(r).apply { isDaemon = true } }
        val hold = java.util.concurrent.CountDownLatch(1)
        try {
            ex.execute { hold.await(10, java.util.concurrent.TimeUnit.SECONDS) }
            val job = a.launchIfAdmitted(scope, ex.asCoroutineDispatcher()) { ran.incrementAndGet() }
            assertNotNull(job)
            assertFalse(a.tryAdmit())                       // admitted and still in flight
            job!!.cancel(); hold.countDown()
            runBlocking { assertNotNull("job did not complete", kotlinx.coroutines.withTimeoutOrNull(10_000) { job.join() }) }
        } finally { hold.countDown(); ex.shutdownNow() }
        assertEquals(0, ran.get())                      // the body never ran, so no in-body finally could have released
        now += 60_000
        assertTrue("in-flight flag wedged after a pre-start cancel", a.tryAdmit())
    }

    @Test fun onlyTheJobCompletionReleasesAndTheGapStillApplies() {
        var now = 5_000L
        val a = RediscoverAdmission(clock = { now })
        val gate = CompletableDeferred<Unit>()
        val first = a.launchIfAdmitted(scope) { gate.await() }!!
        assertNull(a.launchIfAdmitted(scope) { })                  // second caller while the first is running
        gate.complete(Unit); runBlocking { first.join() }
        assertNull(a.launchIfAdmitted(scope) { })                  // finished, but still inside the 60 s gap
        now += 59_999; assertNull(a.launchIfAdmitted(scope) { })
        now += 1
        val gate2 = CompletableDeferred<Unit>()
        val second = a.launchIfAdmitted(scope) { gate2.await() }!!
        // The first job's completion already ran and cannot run again, so it cannot release the second admission.
        assertFalse(a.tryAdmit())
        now += 120_000
        assertFalse("a newer running attempt must stay admitted", a.tryAdmit())
        gate2.complete(Unit); runBlocking { second.join() }
        assertTrue(a.tryAdmit())
    }

    @Test fun aFailingBodyStillReleases() {
        var now = 1L
        val a = RediscoverAdmission(clock = { now })
        val job = a.launchIfAdmitted(scope) { throw IllegalStateException("probe failed") }!!
        runBlocking { job.join() }
        now += 60_000
        assertTrue(a.tryAdmit())
    }
}
