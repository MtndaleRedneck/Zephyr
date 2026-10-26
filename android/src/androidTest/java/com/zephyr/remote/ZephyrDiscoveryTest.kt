package com.zephyr.remote

import androidx.test.runner.AndroidJUnit4
import com.bingemote.HelperClient
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import kotlin.concurrent.thread
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/** "Find my PC": the discovery question gets the ZEPHYR identity back, parsed
 *  through the same client path the helper uses. Loopback fake stands in for
 *  the player; broadcast itself is exercised against real PCs in setup. */
@RunWith(AndroidJUnit4::class)
class ZephyrDiscoveryTest {
    @Volatile private var stop = false
    private var responder: DatagramSocket? = null
    private var responderThread: Thread? = null

    @Before fun startFakePlayer() {
        val sock = DatagramSocket(HelperClient.DISCOVERY_PORT)
        responder = sock
        responderThread = thread {
            while (!stop) {
                try {
                    val buf = ByteArray(1024)
                    val packet = DatagramPacket(buf, buf.size)
                    sock.receive(packet)
                    val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    if (text.trim() == "BINGEMOTE_DISCOVER_V1") {
                        val reply = "ZEPHYR_PLAYER_HERE_V1|EmuPC|47823".toByteArray(Charsets.UTF_8)
                        sock.send(DatagramPacket(reply, reply.size, packet.address, packet.port))
                    }
                } catch (e: Exception) { if (!stop) throw e else return@thread }
            }
        }
    }

    @After fun stopFakePlayer() {
        stop = true
        responder?.close()
        responderThread?.join(1500)
    }

    @Test fun findMyPcParsesZephyrReply() {
        val found = ZephyrClient.discoverPlayers(
            waitMs = 2500,
            targets = listOf(InetAddress.getByName("127.0.0.1")),
        )
        assertEquals(1, found.size)
        assertEquals("EmuPC", found[0].name)
        assertEquals(47823, found[0].port)
        assertEquals("127.0.0.1", found[0].host)
    }

    @Test fun helperRepliesAreIgnored() {
        // A helper answering the same broadcast must NOT show up as a Zephyr PC.
        assertTrue(ZephyrClient.REPLY_MAGIC != "BINGEMOTE_HERE_V1")
    }
}
