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
    }

    private fun get(url: String, range: String? = null): Triple<Int, Map<String, List<String>>, ByteArray> {
        val c = URL(url).openConnection() as HttpURLConnection
        if (range != null) c.setRequestProperty("Range", range)
        val code = c.responseCode
        val body = (if (code in 200..299) c.inputStream else c.errorStream)?.readBytes() ?: ByteArray(0)
        c.disconnect()
        return Triple(code, c.headerFields, body)
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
