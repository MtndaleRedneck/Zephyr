package com.bingemote

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** HelperClient.pair against a loopback MockWebServer (round-6): every refusal carries
 *  the PC's written-for-him detail when there is one, and a plain fallback (never raw
 *  JSON, never blank) when there is not. */
class HelperClientPairTest {

    private lateinit var server: MockWebServer
    private var port = 0

    @Before fun start() {
        server = MockWebServer()
        server.start()
        port = server.port
    }

    @After fun stop() = server.shutdown()

    private fun enqueue(code: Int, body: String) {
        server.enqueue(MockResponse().setResponseCode(code).setBody(body)
            .addHeader("Content-Type", "application/json"))
    }

    private fun pairMessage(code: Int, body: String): String {
        enqueue(code, body)
        try {
            HelperClient.pair("127.0.0.1", port, "424242")
            throw AssertionError("expected pair to fail with $code")
        } catch (e: HelperClient.BadPairCode) {
            return e.msg
        }
    }

    @Test fun `lockout shows the PC's detail`() {
        val msg = pairMessage(429, "{\"error\":\"locked\",\"detail\":\"Too many wrong codes. Press Pair again in the Zephyr VLC Remote window on the PC.\"}")
        assertEquals("Too many wrong codes. Press Pair again in the Zephyr VLC Remote window on the PC.", msg)
    }

    @Test fun `expired code shows the PC's detail`() {
        val msg = pairMessage(410, "{\"error\":\"code expired\",\"detail\":\"That code expired. Press Pair again in the Zephyr VLC Remote window on the PC.\"}")
        assertEquals("That code expired. Press Pair again in the Zephyr VLC Remote window on the PC.", msg)
    }

    @Test fun `phone cap shows the PC's detail`() {
        val msg = pairMessage(409, "{\"error\":\"too many phones\",\"detail\":\"This PC already has 5 phones paired. Remove one in the Zephyr VLC Remote window on the PC to make room.\"}")
        assertEquals("This PC already has 5 phones paired. Remove one in the Zephyr VLC Remote window on the PC to make room.", msg)
    }

    @Test fun `broken store shows the PC's detail`() {
        val msg = pairMessage(503, "{\"error\":\"store broken\",\"detail\":\"The PC's saved pairings could not be read. Pairing is off until that is fixed on the PC - see the Zephyr VLC Remote window.\"}")
        assertEquals("The PC's saved pairings could not be read. Pairing is off until that is fixed on the PC - see the Zephyr VLC Remote window.", msg)
    }

    @Test fun `no detail falls back to plain words, not raw JSON or blank`() {
        val msg = pairMessage(429, "{}")
        assertEquals("Too many tries. Press Pair again in the Zephyr VLC Remote window on the PC.", msg)
    }

    @Test fun `empty body falls back to plain words`() {
        val msg = pairMessage(410, "")
        assertEquals("That code expired - press Pair again in the Zephyr VLC Remote window on the PC.", msg)
    }

    @Test fun `unknown refusal code still says something plain`() {
        val msg = pairMessage(500, "not json")
        assertTrue(msg.isNotBlank())
        assertTrue(!msg.contains("not json"))
    }
}
