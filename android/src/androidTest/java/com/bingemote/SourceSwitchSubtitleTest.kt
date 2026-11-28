package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import java.net.ServerSocket
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

/** Helper-only subtitle work must not run, or survive, when Zephyr Player is the selected source. */
class SourceSwitchSubtitleTest {
    @org.junit.After fun cleanSecurePairing() { ZephyrTestSeed.cleanup(androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().targetContext) }
    private class Stub : AutoCloseable {
        val requests = CopyOnWriteArrayList<String>()
        private val server = TlsFakePlayer.sslContext.serverSocketFactory.createServerSocket(0)
        val port = server.localPort
        @Volatile var listDelayMs = 0L
        @Volatile var loadDelayMs = 0L
        @Volatile var running = true
        init { thread(isDaemon = true) {
            while (running) try {
                server.accept().use { sock ->
                    val reader = sock.getInputStream().bufferedReader()
                    val line = reader.readLine() ?: ""
                    val headers = mutableListOf<String>()
                    while (true) { val h = reader.readLine() ?: break; if (h.isEmpty()) break; headers.add(h) }
                    val len = headers.firstOrNull { it.startsWith("Content-Length:", true) }?.substringAfter(':')?.trim()?.toInt() ?: 0
                    val body = CharArray(len); var n = 0
                    while (n < len) { val k = reader.read(body, n, len - n); if (k < 0) break; n += k }
                    requests.add(line)
                    val isLoad = line.contains("/subtitles/load")
                    Thread.sleep(if (isLoad) loadDelayMs else listDelayMs)
                    val json = if (isLoad) """{"ok":true}""" else """{"mediaId":"opaque-movie","files":[{"id":"opaque-file","name":"Movie.en.srt"}],"unavailable":false}"""
                    val bytes = json.toByteArray()
                    sock.getOutputStream().write(("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n").toByteArray() + bytes)
                }
            } catch (_: Exception) { }
        } }
        fun count(path: String) = requests.count { it.contains(path) }
        override fun close() { running = false; server.close() }
    }

    private suspend fun await(ms: Long = 10000, p: () -> Boolean) {
        val end = System.currentTimeMillis() + ms
        while (System.currentTimeMillis() < end) { if (p()) return; delay(40) }
        assertTrue("condition not reached", p())
    }

    private fun setup(stub: Stub, source: Source): Triple<Store, RemoteState, CoroutineScope> {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        HapticTickTest.seedPrefs(ctx, false, stub.port)
        val store = Store(ctx); ZephyrTestSeed.seedHelper(store, "127.0.0.1", stub.port)
        store.selectedSource = source
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        return Triple(store, RemoteState(ctx, store, scope), scope)
    }

    @Test fun refreshAndLoadDoNothingWhilePlayerIsSelected() = runBlocking {
        Stub().use { stub ->
            val (store, remote, scope) = setup(stub, Source.ZEPHYR)
            try {
                withContext(Dispatchers.Main) { remote.refreshSubtitleFiles() }
                withContext(Dispatchers.Main) { remote.loadSubtitleFile(HelperClient.SubtitleFile("opaque-file", "Movie.en.srt")) }
                delay(1200)
                assertEquals("helper subtitle traffic while Player selected", 0, stub.count("/subtitles"))
                // Control: the same call does reach the helper once VLC is the selected source.
                store.selectedSource = Source.VLC
                withContext(Dispatchers.Main) { remote.refreshSubtitleFiles() }
                await { stub.count("/subtitles/list") == 1 }
            } finally { scope.cancel() }
        }
    }

    @Test fun switchDropsQueuedFileLoadAndClearsFolder() = runBlocking {
        Stub().use { stub ->
            val (store, remote, scope) = setup(stub, Source.VLC)
            try {
                withContext(Dispatchers.Main) { remote.refreshSubtitleFiles() }
                await { remote.subtitleFolder != null }
                val file = remote.subtitleFolder!!.files.single()
                stub.loadDelayMs = 2000
                withContext(Dispatchers.Main) { remote.loadSubtitleFile(file) }
                await { stub.count("/subtitles/load") == 1 } // first load is now in flight at the helper
                withContext(Dispatchers.Main) { remote.loadSubtitleFile(file) } // queued behind it on the same lane
                withContext(Dispatchers.Main) { remote.selectSource(Source.ZEPHYR) }
                assertNull("folder survived the switch", remote.subtitleFolder)
                delay(4500) // the in-flight load finishes; the queued one must be dropped
                assertEquals("queued helper load ran after switching to Player", 1, stub.count("/subtitles/load"))
                assertNull(remote.subtitleFolder)
            } finally { scope.cancel() }
        }
    }

    @Test fun switchDuringListRefreshDiscardsLateAnswer() = runBlocking {
        Stub().use { stub ->
            val (store, remote, scope) = setup(stub, Source.VLC)
            try {
                stub.listDelayMs = 1500
                withContext(Dispatchers.Main) { remote.refreshSubtitleFiles() }
                await { stub.count("/subtitles/list") == 1 }
                withContext(Dispatchers.Main) { remote.selectSource(Source.ZEPHYR) }
                delay(3000)
                assertNull("late list answer landed after the switch", remote.subtitleFolder)
            } finally { scope.cancel() }
        }
    }
}
