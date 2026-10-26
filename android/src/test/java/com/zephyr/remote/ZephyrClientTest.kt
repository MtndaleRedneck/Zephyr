package com.zephyr.remote

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** ZephyrClient against a loopback MockWebServer: routes, auth header, payloads,
 * and the pairing error mapping the phone shows. */
class ZephyrClientTest {

    private lateinit var server: MockWebServer
    private val host = "127.0.0.1"
    private var port = 0

    @Before fun start() {
        server = MockWebServer()
        server.start()
        port = server.port
    }

    @After fun stop() = server.shutdown()

    private fun enqueue(json: String = "{}", code: Int = 200) {
        server.enqueue(MockResponse().setResponseCode(code).setBody(json)
            .addHeader("Content-Type", "application/json"))
    }

    @Test fun `pair trades code for token`() {
        enqueue("{\"token\":\"tok-1\"}")
        assertEquals("tok-1", ZephyrClient.pair(host, port, "123456"))
        val req = server.takeRequest()
        assertEquals("/pair", req.path)
        assertEquals("{\"code\":\"123456\"}", req.body.readUtf8())
    }

    @Test fun `pair maps lockout and cap`() {
        enqueue("{\"error\":\"locked\"}", 429)
        try { ZephyrClient.pair(host, port, "123456"); throw AssertionError("expected throw") }
        catch (e: java.io.IOException) { assertTrue(e.message!!.contains("Too many wrong codes")) }
        enqueue("{\"error\":\"too many phones\"}", 409)
        try { ZephyrClient.pair(host, port, "123456"); throw AssertionError("expected throw") }
        catch (e: java.io.IOException) { assertTrue(e.message!!.contains("too many phones")) }
    }

    @Test fun `library list parses entries and sends token`() {
        enqueue("{\"path\":\"\",\"atRoot\":true,\"entries\":[{\"name\":\"Shows\",\"dir\":true},{\"name\":\"a.mkv\",\"dir\":false}]}")
        val (atRoot, entries) = ZephyrClient.libraryList(host, port, "tok-9")
        assertTrue(atRoot)
        assertEquals(listOf("Shows", "a.mkv"), entries.map { it.name })
        assertEquals(listOf(true, false), entries.map { it.dir })
        val req = server.takeRequest()
        assertEquals("Bearer tok-9", req.getHeader("Authorization"))
        assertEquals("/library/list?path=", req.path)
    }

    @Test fun `library set folder posts folder`() {
        enqueue("{\"libraryFolder\":\"D:\\\\Video\"}")
        ZephyrClient.setLibraryFolder(host, port, "t", "D:\\Video")
        val req = server.takeRequest()
        assertEquals("/library/folder", req.path)
        assertTrue(req.body.readUtf8().contains("D:\\\\Video"))
    }

    @Test fun `control routes hit their paths with payloads`() {
        enqueue(); ZephyrClient.seek(host, port, "t", 90)
        assertEquals("/control/seek", server.takeRequest().path)
        enqueue(); ZephyrClient.volume(host, port, "t", 55)
        server.takeRequest().let { assertEquals("/control/volume", it.path); assertTrue(it.body.readUtf8().contains("55")) }
        enqueue(); ZephyrClient.fullscreen(host, port, "t", true)
        server.takeRequest().let { assertEquals("/control/fullscreen", it.path); assertTrue(it.body.readUtf8().contains("true")) }
    }

    @Test fun `status parses player state`() {
        enqueue("{\"state\":\"Playing\",\"time\":12,\"length\":3600,\"volume\":80,\"muted\":false,\"spu\":-1,\"fullscreen\":false,\"update_available\":null,\"update_check\":\"up_to_date\"}")
        val st = ZephyrClient.controlStatus(host, port, "t")
        assertEquals("Playing", st.getString("state"))
        assertEquals(80, st.getInt("volume"))
        assertEquals("up_to_date", st.getString("update_check"))
    }

    @Test fun `input routes hit their paths with payloads`() {
        // Non-integral values: org.json (Android-derived) serializes integral
        // doubles as longs (-2.0 -> -2), which once made a correct payload fail
        // a contains("-2.0") assertion.
        enqueue(); ZephyrClient.inputMove(host, port, "t", 3.5, -2.5)
        server.takeRequest().let {
            assertEquals("/input/move", it.path)
            val body = it.body.readUtf8()
            assertTrue(body.contains("3.5") && body.contains("-2.5"))
        }
        enqueue(); ZephyrClient.inputKey(host, port, "t", "enter")
        server.takeRequest().let { assertEquals("/input/key", it.path); assertTrue(it.body.readUtf8().contains("enter")) }
        enqueue(); ZephyrClient.inputType(host, port, "t", "hello")
        server.takeRequest().let { assertEquals("/input/type", it.path); assertTrue(it.body.readUtf8().contains("hello")) }
    }

    @Test fun `401 surfaces as IOException`() {
        enqueue("{\"error\":\"unauthorized\"}", 401)
        try { ZephyrClient.controlStatus(host, port, "bad"); throw AssertionError("expected throw") }
        catch (e: java.io.IOException) { assertTrue(e.message!!.contains("401")) }
    }
}
