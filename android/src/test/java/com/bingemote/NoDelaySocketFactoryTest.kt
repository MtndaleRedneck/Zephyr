package com.bingemote

import org.junit.Assert.assertTrue
import org.junit.Test

/** The mouse WebSocket must ship with Nagle off: every socket the factory makes
 * reports TCP_NODELAY set (OkHttp connects the socket later, so this is the only
 * place the option can be applied). */
class NoDelaySocketFactoryTest {
    @Test
    fun createdSocketsHaveNoDelay() {
        NoDelaySocketFactory().createSocket().use { s ->
            assertTrue("factory socket missing TCP_NODELAY", s.tcpNoDelay)
        }
    }

    @Test
    fun everyConnectingOverloadHasNoDelay() {
        java.net.ServerSocket(0, 10, java.net.InetAddress.getByName("127.0.0.1")).use { server ->
            val port = server.localPort
            val lo = java.net.InetAddress.getByName("127.0.0.1")
            val f = NoDelaySocketFactory()
            listOf(
                { f.createSocket("127.0.0.1", port) },
                { f.createSocket(lo, port) },
                { f.createSocket("127.0.0.1", port, lo, 0) },
                { f.createSocket(lo, port, lo, 0) },
            ).forEachIndexed { i, make ->
                make().use { s -> assertTrue("overload $i missing TCP_NODELAY", s.tcpNoDelay) }
            }
        }
    }
}
