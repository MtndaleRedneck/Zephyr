package com.bingemote

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** Round-7 S7-1: postForBody read the error body once and then failure() read it AGAIN,
 *  getting nothing - every /movies/play and /vlc/setup refusal lost the PC's written
 *  detail. These drive playMovie against a loopback MockWebServer and assert the detail
 *  survives, for each refusal shape. */
class HelperClientErrorDetailTest {

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

    private fun playMessage(code: Int, body: String): String {
        enqueue(code, body)
        try {
            HelperClient.playMovie("127.0.0.1", port, "tok", "a/b.mp4", 8080, "pw", false)
        } catch (e: HelperClient.Unreachable) {
            return e.message ?: ""
        }
        throw AssertionError("expected playMovie to fail with $code")
    }

    @Test fun `a 400 refusal keeps the PCs detail`() {
        val msg = playMessage(400, "{\"error\":\"not a video\",\"detail\":\"That file is not a video.\"}")
        assertEquals("That file is not a video.", msg)
    }

    @Test fun `a 404 refusal keeps the PCs detail`() {
        val msg = playMessage(404, "{\"error\":\"vlc not found\",\"detail\":\"VLC does not seem to be installed on this PC (looked in the usual places).\"}")
        assertEquals("VLC does not seem to be installed on this PC (looked in the usual places).", msg)
    }

    @Test fun `a 503 refusal keeps the PCs detail`() {
        val msg = playMessage(503, "{\"error\":\"store broken\",\"detail\":\"The PC's saved pairings could not be read.\"}")
        assertEquals("The PC's saved pairings could not be read.", msg)
    }

    @Test fun `a bare 404 means the PC is too old`() {
        val msg = playMessage(404, "not json at all")
        assertEquals("Zephyr VLC Remote on the PC is too old for this app.", msg)
    }

    @Test fun `a refusal without detail falls back to plain words`() {
        val msg = playMessage(500, "{\"error\":\"launch failed\"}")
        assertEquals("The PC answered with an error (500).", msg)
    }

    @Test fun `a bad token on play still means pair again`() {
        enqueue(403, "{\"error\":\"bad token\"}")
        try {
            HelperClient.playMovie("127.0.0.1", port, "tok", "a/b.mp4", 8080, "pw", false)
            throw AssertionError("expected BadToken")
        } catch (e: HelperClient.BadToken) {
            assertEquals("The PC rejected the pairing token.", e.message)
        }
    }
}
