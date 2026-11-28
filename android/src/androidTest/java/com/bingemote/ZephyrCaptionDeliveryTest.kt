package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import java.net.ServerSocket
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

/** Drives the real RemoteState.pickSubtitle against a fake Zephyr PC on loopback (no phone hardware, no real PC).
 *  CodeRabbit PR3 #10: a Zephyr caption command must reach the PC, and one queued behind a slow command must be
 *  dropped when the subtitle generation moved on (a source switch away and back keeps the source gate open, so only
 *  the generation rule can drop it). */
class ZephyrCaptionDeliveryTest {
    @org.junit.After fun cleanSecurePairing() { ZephyrTestSeed.cleanup(androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().targetContext) }
    private class FakePc : AutoCloseable {
        val captionBodies = CopyOnWriteArrayList<String>()
        val firstGate = java.util.concurrent.CountDownLatch(1)
        @Volatile var gateFirst = false
        private val server = TlsFakePlayer.sslContext.serverSocketFactory.createServerSocket(0)
        val port = server.localPort
        @Volatile var running = true
        init { thread(isDaemon = true) {
            while (running) try {
                val sock = server.accept()
                thread(isDaemon = true) { sock.use { handle(it) } }
            } catch (_: Exception) { }
        } }
        private fun handle(sock: java.net.Socket) {
            val reader = sock.getInputStream().bufferedReader()
            val line = reader.readLine() ?: return
            val headers = mutableListOf<String>()
            while (true) { val h = reader.readLine() ?: break; if (h.isEmpty()) break; headers.add(h) }
            val len = headers.firstOrNull { it.startsWith("Content-Length:", true) }?.substringAfter(':')?.trim()?.toInt() ?: 0
            val body = CharArray(len); var n = 0
            while (n < len) { val k = reader.read(body, n, len - n); if (k < 0) break; n += k }
            if (line.contains("/control/captions")) {
                val first = captionBodies.isEmpty()
                captionBodies.add(String(body, 0, n) + "|" + headers.firstOrNull { it.startsWith("Authorization:", true) })
                if (first && gateFirst) firstGate.await(20, java.util.concurrent.TimeUnit.SECONDS)
            }
            val bytes = "{}".toByteArray()
            sock.getOutputStream().write(("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n").toByteArray() + bytes)
        }
        override fun close() { running = false; server.close() }
    }

    private suspend fun await(ms: Long = 10000, p: () -> Boolean) {
        val end = System.currentTimeMillis() + ms
        while (System.currentTimeMillis() < end) { if (p()) return; delay(40) }
        assertTrue("condition not reached", p())
    }

    private fun setup(pc: FakePc): Pair<RemoteState, CoroutineScope> {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        HapticTickTest.seedPrefs(ctx, false, pc.port)
        val store = Store(ctx)
        ZephyrTestSeed.seed(store, "127.0.0.1", pc.port)
        store.selectedSource = Source.ZEPHYR
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        return RemoteState(ctx, store, scope) to scope
    }
    @Test fun pickedCaptionReachesTheZephyrPc() = runBlocking {
        FakePc().use { pc ->
            val (remote, scope) = setup(pc)
            try {
                withContext(Dispatchers.Main) { remote.pickSubtitle(2) }
                await { pc.captionBodies.size == 1 }
                assertTrue(pc.captionBodies[0], pc.captionBodies[0].contains("\"track\":2"))
                assertTrue(pc.captionBodies[0], pc.captionBodies[0].contains("Bearer ${ZephyrTestSeed.TOKEN}"))
            } finally { scope.cancel() }
        }
    }

    /** Positive control for the negative test below: the same queued-behind-a-held-caption setup WITHOUT the source switch.
     *  Both captions must be delivered in order, so the negative test's missing "3" can only come from the generation rule,
     *  not from seeded prefs, the held request, or the fake PC dropping a queued request. */
    @Test fun captionQueuedBehindASlowOneIsDeliveredWhenNothingChanged() = runBlocking {
        FakePc().use { pc ->
            pc.gateFirst = true
            val (remote, scope) = setup(pc)
            try {
                withContext(Dispatchers.Main) { remote.pickSubtitle(2) }
                await { pc.captionBodies.size == 1 }
                withContext(Dispatchers.Main) { remote.pickSubtitle(3) }
                pc.firstGate.countDown()
                await { pc.captionBodies.size == 2 }
                assertTrue(pc.captionBodies[1], pc.captionBodies[1].contains("\"track\":3"))
            } finally { pc.firstGate.countDown(); scope.cancel() }
        }
    }
    @Test fun captionQueuedBehindASlowOneIsDroppedAfterTheGenerationMoves() = runBlocking {
        FakePc().use { pc ->
            pc.gateFirst = true                                        // the fake PC holds the first caption until released
            val (remote, scope) = setup(pc)
            try {
                withContext(Dispatchers.Main) { remote.pickSubtitle(2) }   // occupies the worker (blocked on the gate)
                await { pc.captionBodies.size == 1 }
                withContext(Dispatchers.Main) { remote.pickSubtitle(3) }   // queued behind it, current generation
                withContext(Dispatchers.Main) { remote.selectSource(Source.VLC); remote.selectSource(Source.ZEPHYR) } // bumps the generation; source gate open again
                pc.firstGate.countDown()                                   // release: the worker now reaches the queued one
                // FIFO: a caption sent in the new generation arrives after any (wrongly) sent stale one, so this is a deterministic barrier.
                withContext(Dispatchers.Main) { remote.pickSubtitle(4) }
                await { pc.captionBodies.size >= 2 }
                assertTrue("only the new-generation caption may follow: " + pc.captionBodies, pc.captionBodies[1].contains("\"track\":4"))
                assertEquals("stale caption must not be sent: " + pc.captionBodies, 2, pc.captionBodies.size)
            } finally { pc.firstGate.countDown(); scope.cancel() }
        }
    }
}
