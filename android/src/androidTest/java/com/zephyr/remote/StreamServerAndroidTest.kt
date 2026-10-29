package com.zephyr.remote

import android.net.Uri
import androidx.test.runner.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URL
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/** Loopback integration test: real sockets, real Range handling, real token
 *  checks, against a synthetic media file. SAF/provider paths use a file:// URI
 *  (contentResolver-backed, same descriptor route as SAF). */
@RunWith(AndroidJUnit4::class)
class StreamServerAndroidTest {

    private lateinit var server: StreamServer
    private lateinit var file: File
    private lateinit var movieUrl: String
    private val bytes = ByteArray(1000) { (it % 251).toByte() }

    @Before
    fun setUp() {
        android.util.Log.i("ZephyrAudit", "APP_PID=${android.os.Process.myPid()}")
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        file = File(context.cacheDir, "synthetic-movie.bin").apply { writeBytes(bytes) }
        server = StreamServer(context, InetAddress.getByName("127.0.0.1"), 0)
        server.start()
        movieUrl = server.addMovie(Uri.fromFile(file), "synthetic-movie.bin")
    }

    @After
    fun tearDown() {
        server.stop()
        file.delete()
        // Make abandoned CloseGuard-owned objects visible in the captured app log.
        System.gc(); System.runFinalization(); Thread.sleep(100)
    }

    private fun get(url: String, range: String? = null): Triple<Int, Map<String, List<String>>, ByteArray> {
        val c = URL(url).openConnection() as HttpURLConnection
        if (range != null) c.setRequestProperty("Range", range)
        val code = c.responseCode
        val body = (if (code in 200..299) c.inputStream else c.errorStream)?.use { it.readBytes() } ?: ByteArray(0)
        c.disconnect()
        return Triple(code, c.headerFields, body)
    }

    /** Actual pipe/copy/stop lifecycle. Deliberately not a SAF permission/grant test. */
    @Test fun stalledPipeStopReturnsAndWorkersDrain() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val writers = java.util.concurrent.CopyOnWriteArrayList<android.os.ParcelFileDescriptor>()
        val opened = java.util.concurrent.atomic.AtomicInteger()
        server.stop()
        server = StreamServer(context, InetAddress.getByName("127.0.0.1"), 0, openDescriptor = { _, _ ->
            android.os.ParcelFileDescriptor.createPipe().also { writers.add(it[1]); opened.incrementAndGet() }[0]
        })
        server.start()
        val url = server.addMovie(Uri.parse("content://synthetic-pipe/stall"), "stall.mkv")
        val client = kotlin.concurrent.thread { runCatching { get(url) } }
        try {
            val deadline = android.os.SystemClock.elapsedRealtime() + 5000
            while (opened.get() < 2 && android.os.SystemClock.elapsedRealtime() < deadline) Thread.sleep(10)
            assertTrue("copy pipe was opened", opened.get() >= 2)
            val before = android.os.SystemClock.elapsedRealtime(); server.stop()
            assertTrue("stop must return promptly", android.os.SystemClock.elapsedRealtime() - before < 2000)
            client.join(5000); assertTrue("stalled client ended", !client.isAlive)
            val until = android.os.SystemClock.elapsedRealtime()+5000
            while (server.openStreams.get()>0 && android.os.SystemClock.elapsedRealtime()<until) Thread.sleep(10)
            assertEquals(0, server.openStreams.get())
            // Keep producer ends open until after drain assertions: no false release from test teardown.
            assertTrue(context.cacheDir.listFiles().orEmpty().none { it.name.endsWith(".part") && it.name.startsWith("stream-") })
        } finally {
            server.stop(); writers.forEach { runCatching { it.close() } }; client.join(5000)
        }
    }
    @Test fun stalledWriterIsClosedByDeadline() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val large = File(context.cacheDir, "stalled-writer.bin")
        java.io.RandomAccessFile(large,"rw").use { it.setLength(128L*1024*1024) }
        val url = URL(server.addMovie(Uri.fromFile(large), "large"))
        val sock = java.net.Socket(); sock.receiveBufferSize=1024; sock.connect(java.net.InetSocketAddress(url.host,url.port))
        sock.getOutputStream().write("GET ${url.path} HTTP/1.1\r\nHost: localhost\r\n\r\n".toByteArray())
        try {
            Thread.sleep(200)
            val until=android.os.SystemClock.elapsedRealtime()+65000
            while(server.openStreams.get()>0 && android.os.SystemClock.elapsedRealtime()<until) Thread.sleep(100)
            assertEquals("stalled writer released",0,server.openStreams.get())
        } finally {sock.close();large.delete()}
    }
    @Test fun restartPurgesStaleCacheAndKeepsOtherCache() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val stale = File(context.cacheDir, "stream-old-dead.part").apply { writeText("old") }
        val unrelated = File(context.cacheDir, "unrelated.txt").apply { writeText("keep") }
        server.stop()
        server = StreamServer(context, InetAddress.getByName("127.0.0.1"), 0)
        server.start()
        assertTrue(!stale.exists()); assertTrue(unrelated.exists()); unrelated.delete()
    }
    @Test fun rangeErrorsAndUnsupportedRanges_realSockets() {
        for (range in listOf("bytes=1000-", "bytes=900-800")) {
            val (code, headers, body) = get(movieUrl, range)
            assertEquals(416, code); assertEquals(0, body.size)
            assertTrue(headers.entries.any { it.key.equals("Content-Range", true) && it.value.contains("bytes */1000") })
        }
        for (range in listOf("items=0-9", "bytes=-9", "bytes=0-9,20-30", "bytes=garbage")) {
            val (code, _, body) = get(movieUrl, range)
            assertEquals(200, code); assertArrayEquals(bytes, body)
        }
    }

    @Test
    fun abandoned100LargeSeeks_recoverAndStillLoadSubtitle() {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val large=File(context.cacheDir,"abandoned-seeks.bin")
        java.io.RandomAccessFile(large,"rw").use { it.setLength(16L*1024*1024) }
        val sub=File(context.cacheDir,"abandoned-seeks.srt").apply { writeText("1\n00:00:00,000 --> 00:00:03,000\nHello\n") }
        try {
            val url=server.addMovie(Uri.fromFile(large),"large",listOf(Uri.fromFile(sub)))
            repeat(100) { i ->
                val c=URL(url).openConnection() as HttpURLConnection;c.setRequestProperty("Range","bytes=${i*1000}-");c.connectTimeout=3000;c.readTimeout=3000
                try { assertEquals(206,c.responseCode);c.inputStream.use { assertTrue(it.read(ByteArray(1024))>0) } } finally { c.disconnect() }
            }
            assertArrayEquals(sub.readBytes(),get("$url/sub/0").third)
            val until=android.os.SystemClock.elapsedRealtime()+5000
            while(server.openStreams.get()>0 && android.os.SystemClock.elapsedRealtime()<until)Thread.sleep(10)
            assertEquals(0,server.openStreams.get())
        } finally {large.delete();sub.delete()}
    }

    @Test
    fun rapid100SeeksAndSubtitle_realSocketsPreserveBytes() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val sub = File(context.cacheDir, "rapid-seek.srt").apply { writeText("1\n00:00:00,000 --> 00:00:03,000\nHello\n") }
        try {
            val url = server.addMovie(Uri.fromFile(file), "movie", listOf(Uri.fromFile(sub)))
            // Warm the HTTP implementation before measuring actual process FD counts.
            get(url, "bytes=0-16")
            fun settledFdCount(): Int {
                val until = android.os.SystemClock.elapsedRealtime() + 5000
                while (server.openStreams.get() > 0 && android.os.SystemClock.elapsedRealtime() < until) Thread.sleep(10)
                assertEquals("all request workers drained", 0, server.openStreams.get())
                System.gc(); System.runFinalization(); Thread.sleep(100)
                return java.io.File("/proc/self/fd").list()?.size ?: error("FD count unavailable")
            }
            val beforeFds = settledFdCount()
            repeat(200) { i ->
                val start = i * 7 % 900
                val (code, _, body) = get(url, "bytes=$start-${start+16}")
                assertEquals(206, code); assertArrayEquals(bytes.copyOfRange(start,start+17),body)
                if (i % 10 == 0) assertArrayEquals(sub.readBytes(),get("$url/sub/0").third)
            }
            val afterFds = settledFdCount()
            android.util.Log.i("ZephyrAudit", "FD_COUNT before=$beforeFds after=$afterFds allowance=10")
            assertTrue("per-request FDs retained: before=$beforeFds after=$afterFds", afterFds <= beforeFds + 10)
        } finally { sub.delete() }
    }

    @Test
    fun oversizedHeader_refusedWithoutServing() {
        val u = URL(movieUrl)
        java.net.Socket(u.host, u.port).use { socket ->
            socket.soTimeout = 3000
            socket.getOutputStream().write(("GET ${u.path} HTTP/1.1\r\nX-Large: " + "x".repeat(9000) + "\r\n\r\n").toByteArray())
            assertEquals(-1, socket.getInputStream().read())
        }
        org.junit.Assert.assertFalse(server.servedAny)
    }

    @Test
    fun connectionCap_doesNotCreateUnboundedWorkers() {
        val u = URL(movieUrl)
        val sockets = (1..12).map { java.net.Socket(u.host, u.port) }
        try {
            Thread.sleep(250)
            assertTrue(server.openStreams.get() <= 4)
            org.junit.Assert.assertFalse(server.servedAny)
        } finally { sockets.forEach { it.close() } }
    }

    @Test
    fun fullGet_200_allBytes() {
        val (code, headers, body) = get(movieUrl)
        assertEquals(200, code)
        assertArrayEquals(bytes, body)
        assertEquals("1000", headers["Content-Length"]?.firstOrNull())
    }

    @Test
    fun rangeGet_206_correctSpanAndHeader() {
        val (code, headers, body) = get(movieUrl, "bytes=10-19")
        assertEquals(206, code)
        assertEquals(10, body.size)
        assertArrayEquals(bytes.copyOfRange(10, 20), body)
        assertEquals("bytes 10-19/1000", headers["Content-Range"]?.firstOrNull())
    }

    @Test
    fun openEndedRange_clampsToEnd() {
        val (code, headers, body) = get(movieUrl, "bytes=990-")
        assertEquals(206, code)
        assertEquals(10, body.size)
        assertEquals("bytes 990-999/1000", headers["Content-Range"]?.firstOrNull())
    }

    @Test
    fun wrongToken_403() {
        val bad = movieUrl.replace(Regex("/s/[^/]+/"), "/s/000000000000000000000000000000000000/")
        val (code, _, _) = get(bad)
        assertEquals(403, code)
    }

    @Test
    fun unknownMovie_404() {
        val (code, _, _) = get("$movieUrl".replaceAfterLast('/', "99"))
        assertEquals(404, code)
    }

    @Test
    fun urlShape_carriesSessionTokenNotPairingToken() {
        assertTrue(movieUrl.matches(Regex("http://127\\.0\\.0\\.1:\\d+/s/[0-9a-f]{36}/1")))
    }
}
