package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.AndroidJUnit4
import java.util.concurrent.TimeUnit
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Phone-origin playback routing (phone -> VLC allowed when VLC is
 * the selected target): the phone-served URL reaches the SELECTED PC ONLY -
 * no fallthrough, no traffic to the other program.
 */
@RunWith(AndroidJUnit4::class)
class StreamTargetTest {

    private lateinit var zephyr: MockWebServer
    private lateinit var vlc: MockWebServer
    private lateinit var store: Store

    @Before fun setUp() {
        zephyr = MockWebServer(); zephyr.start()
        vlc = MockWebServer(); vlc.start()
        store = Store(InstrumentationRegistry.getInstrumentation().targetContext)
        store.zephyrHost = "127.0.0.1"; store.zephyrPort = zephyr.port; store.zephyrToken = "zt"
        store.host = "127.0.0.1"; store.vlcPort = vlc.port; store.vlcPassword = "pw"
    }

    @After fun tearDown() {
        store.selectedSource = Source.VLC
        store.zephyrToken = ""
        zephyr.shutdown(); vlc.shutdown()
    }

    private fun ok() = MockResponse().setResponseCode(200).setBody("{}")

    @Test fun vlcSelected_phoneMovieGoesToVlcOnly() {
        store.selectedSource = Source.VLC
        vlc.enqueue(ok())
        StreamTarget.play(store, "http://192.168.1.9:47888/movie/abc/Phone%20Movie.mkv",
            listOf("http://192.168.1.9:47888/sub/0"))
        val hit = vlc.takeRequest(3, TimeUnit.SECONDS)
        assertNotNull("VLC never got in_play", hit)
        assertTrue(hit!!.path!!.startsWith("/requests/status.json?command=in_play&input="))
        assertTrue(hit.path!!.contains("Phone%2520Movie"))
        assertTrue("subtitle must ride as a :sub-file option",
            hit.path!!.contains("option=%3Asub-file%3Dhttp"))
        assertNull("zephyr got traffic while VLC selected", zephyr.takeRequest(1, TimeUnit.SECONDS))
    }

    @Test fun zephyrSelected_phoneMovieGoesToZephyrOnly() {
        store.selectedSource = Source.ZEPHYR
        zephyr.enqueue(ok())
        StreamTarget.play(store, "http://192.168.1.9:47888/movie/abc/Phone%20Movie.mkv", listOf("http://192.168.1.9:47888/sub/0"))
        val hit = zephyr.takeRequest(3, TimeUnit.SECONDS)
        assertNotNull("zephyr never got /play", hit)
        assertTrue(hit!!.path!!.startsWith("/play"))
        assertNull("VLC got traffic while Zephyr selected", vlc.takeRequest(1, TimeUnit.SECONDS))
    }
}
