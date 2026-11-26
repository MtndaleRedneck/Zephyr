package com.bingemote

import com.zephyr.remote.ZephyrClient
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import java.util.concurrent.TimeUnit

/** CodeRabbit PR3 #10, end to end on the JVM: a Zephyr caption command queued with the current generation is taken
 *  from the real queue and its block performs the real ZephyrClient.captions request against a loopback server;
 *  a command queued before a generation bump is dropped and sends nothing. Bounded waits, no sleeps. */
class CommandQueueTest {
    private lateinit var server: MockWebServer
    private var generation = 0
    private lateinit var queue: CommandQueue

    @Before fun start() {
        server = MockWebServer(); server.start()
        queue = CommandQueue(currentSubtitleGeneration = { generation })
    }
    @After fun stop() = server.shutdown()

    private fun captions(track: Int): () -> Unit = { ZephyrClient.captions("127.0.0.1", server.port, "tok", track) }
    private fun sub(gen: Int, track: Int) = CommandQueue.Cmd("sub", System.currentTimeMillis(), gen, captions(track))

    @Test fun currentGenerationCaptionIsTakenAndDelivered() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(200).setBody("{}"))
        queue.trySend(sub(generation, 2))
        withTimeout(5000) { queue.next().block() }
        val req = server.takeRequest(5, TimeUnit.SECONDS)!!
        assertEquals("/control/captions", req.path)
        assertEquals("Bearer tok", req.getHeader("Authorization"))
        assertEquals(2, org.json.JSONObject(req.body.readUtf8()).getInt("track"))
    }

    @Test fun captionQueuedBeforeGenerationBumpIsDroppedAndSendsNothing() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(200).setBody("{}"))
        queue.trySend(sub(generation, 2))
        generation++                                  // source or movie changed while it waited
        queue.trySend(CommandQueue.Cmd("playpause", System.currentTimeMillis(), Int.MIN_VALUE) {})
        val next = withTimeout(5000) { queue.next() }  // the stale caption is skipped, the next command is returned
        assertEquals("playpause", next.kind)
        assertNull(server.takeRequest(300, TimeUnit.MILLISECONDS))
    }

    @Test fun commandWithSentinelGenerationNeverRunsAsSubtitle() = runBlocking {
        queue.trySend(CommandQueue.Cmd("sub", System.currentTimeMillis(), Int.MIN_VALUE, captions(1)))
        queue.trySend(CommandQueue.Cmd("seek", System.currentTimeMillis(), Int.MIN_VALUE) {})
        assertEquals("seek", withTimeout(5000) { queue.next() }.kind)
        assertNull(server.takeRequest(300, TimeUnit.MILLISECONDS))
    }

    @Test fun volumeBurstFoldsAndOldTapExpiresButMuteAndSubDoNot() = runBlocking {
        var folded = 0; var expired = mutableListOf<String>()
        var t = 10_000L
        val q = CommandQueue({ 0 }, { t }, { folded++ }, { expired.add(it.kind) })
        q.trySend(CommandQueue.Cmd("volume", t, Int.MIN_VALUE) {}); q.trySend(CommandQueue.Cmd("volume", t, Int.MIN_VALUE) {})
        assertEquals("volume", q.next().kind); assertEquals(1, folded)
        q.trySend(CommandQueue.Cmd("seek", t - CmdPolicy.EXPIRY_MS - 1, Int.MIN_VALUE) {})
        q.trySend(CommandQueue.Cmd("mute", t - 99_999, Int.MIN_VALUE) {})
        assertEquals("mute", q.next().kind); assertEquals(listOf("seek"), expired)
    }
}
