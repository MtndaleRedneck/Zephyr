package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.AndroidJUnit4
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Source-routing proof (protocol.md Source routing): with Zephyr selected, input
 * frames reach the Zephyr Player ONLY - zero traffic to the helper, and no
 * fallthrough when Zephyr is unpaired. Load-bearing: the phone's selector is the
 * one arbitration point; a leak would drive the wrong PC.
 */
@RunWith(AndroidJUnit4::class)
class InputRoutingTest {

    private lateinit var zephyr: MockWebServer
    private lateinit var helper: MockWebServer
    private lateinit var store: Store
    private lateinit var remote: RemoteState

    @Before fun setUp() {
        zephyr = MockWebServer(); zephyr.start()
        helper = MockWebServer(); helper.start()
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        store = Store(context)
        // Isolate: a real paired state must not leak in from an earlier class.
        store.zephyrHost = "127.0.0.1"; store.zephyrPort = zephyr.port; store.zephyrToken = "zt"
        store.host = "127.0.0.1"; store.helperPort = helper.port; store.helperToken = "ht"
        remote = RemoteState(context, store, kotlinx.coroutines.MainScope())
        remote.publishAddress("127.0.0.1", helper.port, "ht")
    }

    @After fun tearDown() {
        store.selectedSource = Source.VLC
        store.zephyrToken = ""
        zephyr.shutdown(); helper.shutdown()
    }

    private fun ok() = MockResponse().setResponseCode(200).setBody("{\"ok\":true}")

    @Test fun zephyrSelected_framesGoOnlyToZephyr() = runBlocking {
        store.selectedSource = Source.ZEPHYR
        zephyr.enqueue(ok())
        InputRouter.move(remote, 3.5f, -2f)
        val hit = zephyr.takeRequest(3, TimeUnit.SECONDS)
        assertNotNull("zephyr never got the frame", hit)
        assertEquals("/input/move", hit!!.path)
        assertEquals("Bearer zt", hit.getHeader("Authorization"))
        assertNull("helper got traffic while Zephyr selected", helper.takeRequest(1, TimeUnit.SECONDS))
    }

    @Test fun zephyrSelectedUnpaired_noFallthroughToHelper() {
        store.selectedSource = Source.ZEPHYR
        store.zephyrToken = ""
        try {
            InputRouter.button(remote, "left", true)
            fail("unpaired Zephyr selection must not send anywhere")
        } catch (e: java.io.IOException) {
            assertTrue(e.message!!.contains("isn't paired"))
        }
        assertNull("helper got traffic on unpaired Zephyr selection", helper.takeRequest(1, TimeUnit.SECONDS))
        assertEquals(0, zephyr.requestCount)
    }

    @Test fun vlcSelected_framesGoToHelperNotZephyr() = runBlocking {
        store.selectedSource = Source.VLC
        helper.enqueue(ok())
        InputRouter.key(remote, "enter")
        val hit = helper.takeRequest(3, TimeUnit.SECONDS)
        assertNotNull("helper never got the frame", hit)
        assertEquals("/key", hit!!.path)
        assertNull("zephyr got traffic while VLC selected", zephyr.takeRequest(1, TimeUnit.SECONDS))
    }
}
