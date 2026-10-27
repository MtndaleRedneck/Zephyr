package com.zephyr.remote

import com.bingemote.HelperClient
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import kotlin.concurrent.thread
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import androidx.test.runner.AndroidJUnit4

/** "Find my PC": the discovery question gets the ZEPHYR identity back, parsed
 *  through the same client path the helper uses. Loopback fakes stand in for
 *  the player; broadcast itself is exercised against real PCs in setup.
 *  Round-3 A2: the fakes bind the PLAYER's ports (8490 current + 8489 legacy),
 *  never the helper's - the helper moved to 8491 when it became Zephyr VLC
 *  Remote, and a test bound there finds nothing by design. */
@RunWith(AndroidJUnit4::class)
class ZephyrDiscoveryTest {
    @Volatile private var stop = false
    private val responders = mutableListOf<DatagramSocket>()
    private val threads = mutableListOf<Thread>()

    private fun startFakePlayer(port: Int, pcName: String = "EmuPC") {
        val sock = DatagramSocket(port)
        responders += sock
        threads += thread {
            while (!stop) {
                try {
                    val buf = ByteArray(1024)
                    val packet = DatagramPacket(buf, buf.size)
                    sock.receive(packet)
                    val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    if (text.trim() == "BINGEMOTE_DISCOVER_V1") {
                        val reply = "${ZephyrClient.REPLY_MAGIC}|$pcName|47823".toByteArray(Charsets.UTF_8)
                        sock.send(DatagramPacket(reply, reply.size, packet.address, packet.port))
                    }
                } catch (e: Exception) { if (!stop) throw e else return@thread }
            }
        }
    }

    @After fun stopFakePlayers() {
        stop = true
        responders.forEach { runCatching { it.close() } }
        threads.forEach { it.join(1500) }
    }

    @Test fun findMyPcParsesZephyrReply() {
        startFakePlayer(ZephyrClient.DISCOVERY_PORT)
        val found = ZephyrClient.discoverPlayers(
            waitMs = 2500,
            targets = listOf(InetAddress.getByName("127.0.0.1")),
        )
        assertEquals(1, found.size)
        assertEquals("EmuPC", found[0].name)
        assertEquals(47823, found[0].port)
        assertEquals("127.0.0.1", found[0].host)
    }

    @Test fun legacyPortAlsoFound() {
        // 1.1-era players still answer on the legacy port; the scan covers both.
        startFakePlayer(ZephyrClient.LEGACY_DISCOVERY_PORT, pcName = "OldPC")
        val found = ZephyrClient.discoverPlayers(
            waitMs = 2500,
            targets = listOf(InetAddress.getByName("127.0.0.1")),
        )
        assertEquals(1, found.size)
        assertEquals("OldPC", found[0].name)
    }

    @Test fun helperRepliesAreIgnored() {
        // A helper answering the same broadcast must NOT show up as a Zephyr PC.
        assertTrue(ZephyrClient.REPLY_MAGIC != HelperClient.REPLY_MAGIC)
    }
}
