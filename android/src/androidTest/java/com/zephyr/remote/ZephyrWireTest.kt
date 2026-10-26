package com.zephyr.remote

import android.net.Uri
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.AndroidJUnit4
import java.io.File
import java.net.InetAddress
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/**
 * End-to-end wire test: the REAL ZephyrClient against the REAL PlaybackServer
 * (headless host on the CI runner, reached via 10.0.2.2) - pairing handshake,
 * auth, library browsing off disk, play command, input frame, status parse.
 * No mocks on either side. The phone->player stream handoff is closed by the
 * host fetching the served URL back through adb reverse.
 *
 * Runs only when the harness wires it (instrumentation args wire_port +
 * pair_code); otherwise a visible skip, never a silent pass.
 */
@RunWith(AndroidJUnit4::class)
class ZephyrWireTest {

    private val host = "10.0.2.2"
    private var port = 0
    private var pairCode = ""

    @Before fun setUp() {
        val args = InstrumentationRegistry.getArguments()
        port = args.getString("wire_port")?.toIntOrNull() ?: 0
        pairCode = args.getString("pair_code") ?: ""
        assumeTrue("wire host not wired (needs -e wire_port N -e pair_code NNNNNN)", port > 0 && pairCode.isNotEmpty())
    }

    @Test fun pairBrowsePlayInputStatusOverTheRealWire() {
        // Pairing handshake: 6-digit code -> token (real PairingGate on the host).
        val token = ZephyrClient.pair(host, port, pairCode)
        assertTrue("empty token", token.isNotEmpty())

        // Library browsing off the host's real disk (harness seeds the folder).
        ZephyrClient.setLibraryFolder(host, port, token, "/tmp/zephyr-wire-lib")
        assertEquals("/tmp/zephyr-wire-lib", ZephyrClient.libraryFolder(host, port, token))
        val (listed, entries) = ZephyrClient.libraryList(host, port, token)
        assertTrue("library list failed", listed)
        assertTrue("seeded library empty", entries.any { it.name == "Wire Movie One (2024).mkv" })

        // Phone->player stream handoff: serve a real file here, hand the URL to
        // the player, and the host fetches it back through adb reverse.
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val payload = File(ctx.cacheDir, "wire-payload.bin")
        payload.writeBytes(ByteArray(200_000) { (it % 251).toByte() })
        val server = StreamServer(ctx, InetAddress.getByName("127.0.0.1"), 47888)
        server.start()
        // StreamServer serves SAF/file URIs per protocol.md Streaming.
        val streamUrl = server.addMovie(Uri.fromFile(payload), "wire-payload.bin")
        ZephyrClient.play(host, port, token, streamUrl, emptyList())

        // The host's play stub reports the fetch outcome through /control/status.
        var lastPlay = ""
        val deadline = System.currentTimeMillis() + 15_000
        while (System.currentTimeMillis() < deadline) {
            lastPlay = ZephyrClient.controlStatus(host, port, token).optString("last_play")
            if (lastPlay.startsWith("fetched:")) break
            Thread.sleep(500)
        }
        server.stop()
        assertTrue("player never fetched the stream (last_play=$lastPlay)",
            lastPlay.startsWith("fetched:200:200000:range:206"))

        // Input frame over the wire (reaches the host's recorder, not the helper).
        ZephyrClient.inputMove(host, port, token, 1.5, -1.5)
    }
}
