package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Candidate (unwired) remembered-PC move: only an address that proves the stored key is returned, nothing is sent to anyone else. */
class HelperRediscoverMoveTest {
    private fun h(host: String, port: Int, name: String = "Office PC") = DiscoveredHelper(host, port, name)

    @Test fun movedPcWithTheStoredKeyIsFoundAndImpostorIsNot() {
        val real = TlsFixture(); val impostor = TlsFixture()
        try {
            val saved = real.credential()
            // The "old address" is dead; the real PC now answers on real.port. The impostor copies the name on another port.
            val oldPort = 1
            val rec = PairedCredential.of(saved.pcId, saved.pinHex, "127.0.0.1", oldPort, saved.token)!!.encode()
            val v = HelperRediscover.findVerifiedMove("127.0.0.1", oldPort, "Office PC", rec,
                listOf(h("127.0.0.1", impostor.port), h("127.0.0.1", real.port)), addressOk = { true })
            assertNotNull(v)
            assertEquals(real.port, v!!.port); assertEquals(rec, v.record); assertEquals(saved.pcId, v.pcId)
            // Nothing but handshakes reached either server, and the impostor did not latch the real PC as key-changed.
            assertEquals(0, real.server.requestCount); assertEquals(0, impostor.server.requestCount)
            assertFalse(KeyChangeState.isChanged(saved.pcId))
        } finally { real.close(); impostor.close() }
    }

    @Test fun impostorAloneYieldsNothingAndLatchesNothing() {
        val real = TlsFixture(); val impostor = TlsFixture()
        try {
            val saved = real.credential()
            val rec = saved.encode()
            val v = HelperRediscover.findVerifiedMove("127.0.0.1", 1, "Office PC", rec, listOf(h("127.0.0.1", impostor.port)), addressOk = { true })
            assertNull(v)
            assertFalse(KeyChangeState.isChanged(saved.pcId))
            assertEquals(0, impostor.server.requestCount)
        } finally { real.close(); impostor.close() }
    }

    @Test fun boundsAreEnforcedBeforeAnyConnection() {
        val calls = java.util.concurrent.atomic.AtomicInteger()
        val verify: (String, Int) -> SecureHttp.VerifiedAddress? = { _, _ -> calls.incrementAndGet(); null }
        val rec = TlsFixture().also { it.close() }.credential().encode()
        val many = (1..10).map { h("192.168.1.$it", 8492) } +
            listOf(h("192.168.1.99", 8492, "Den PC"), h("192.168.1.50", 8492), h("8.8.8.8", 8492), h("example.com", 8492), h("192.168.1.60", 0), h("192.168.1.61", 70000))
        assertNull(HelperRediscover.findVerifiedMove("192.168.1.50", 8492, "Office PC", rec, many, verify = verify))
        assertEquals(HelperRediscover.MAX_CANDIDATES, calls.get())   // cap; other name, same address, public, hostname, bad ports never tried
        calls.set(0)
        // The saved name only orders candidates: a renamed PC is still tried, named ones first, all inside the cap.
        val order = mutableListOf<String>()
        HelperRediscover.findVerifiedMove("192.168.1.50", 8492, "Office PC", rec,
            listOf(h("192.168.1.71", 8492, "Other"), h("192.168.1.72", 8492, "Other"), h("192.168.1.73", 8492, "Office PC"), h("192.168.1.74", 8492, "office pc")),
            verify = { host, _ -> order.add(host); null })
        assertEquals(listOf("192.168.1.73", "192.168.1.74", "192.168.1.71"), order)
        order.clear()
        HelperRediscover.findVerifiedMove("192.168.1.50", 8492, "", rec, listOf(h("192.168.1.71", 8492, "Renamed")), verify = { host, _ -> order.add(host); null })
        assertEquals(listOf("192.168.1.71"), order)
    }

    @Test fun privateLiteralsOnly() {
        assertTrue(HelperRediscover.isPrivateLiteral("192.168.1.5")); assertTrue(HelperRediscover.isPrivateLiteral("10.0.0.2"))
        assertTrue(HelperRediscover.isPrivateLiteral("172.20.1.1")); assertTrue(HelperRediscover.isPrivateLiteral("169.254.3.4"))
        assertTrue(HelperRediscover.isPrivateLiteral("[fe80::1]"))
        assertFalse(HelperRediscover.isPrivateLiteral("8.8.8.8")); assertFalse(HelperRediscover.isPrivateLiteral("example.com"))
        assertFalse(HelperRediscover.isPrivateLiteral("127.0.0.1")); assertFalse(HelperRediscover.isPrivateLiteral(""))
    }

    @Test fun malformedOrNonLiteralHostsAreRejectedWithoutAnyLookup() {
        for (bad in listOf("1.2.3", "192.168.1", "192.168.1.1.1", "999.1.1.1", "192.168.01.1", "192.168.1.256", "192.168.1.1 ", " 10.0.0.1",
            "10.0.0.1%eth0", "fe80::1%eth0", "localhost", "router.local", "1:2:3:4:5:6:7:8:9", "fe80:::1", "gggg::1", "[fe80::1", "fe80::1]",
            "::ffff:8.8.8.8", "2001:db8::1", "0x7f000001", "192.168.1.1/24", "192.168.1.1:80"))
            assertFalse("accepted: '$bad'", HelperRediscover.isPrivateLiteral(bad))
        assertTrue(HelperRediscover.isPrivateLiteral("fe80::1")); assertTrue(HelperRediscover.isPrivateLiteral("fd12:3456::1"))
    }

    @Test fun verifyRejectsAMalformedRecordAndABadPort() {
        assertNull(SecureHttp.verify("nope", "127.0.0.1", 8492))
        val rec = TlsFixture().also { it.close() }.credential().encode()
        assertNull(SecureHttp.verify(rec, "127.0.0.1", 0))
    }

    @Test fun sameAddressInAnyTextFormIsOneKeyAndTheSavedEndpointIsExcluded() {
        assertEquals(HelperRediscover.addressKey("fd00::1", 8492), HelperRediscover.addressKey("[FD00:0:0:0:0:0:0:1]", 8492))
        assertEquals(HelperRediscover.addressKey("192.168.1.5", 8492), HelperRediscover.addressKey("::ffff:c0a8:105", 8492))
        assertNull(HelperRediscover.parseLiteral("::ffff:192.168.1.5"))     // mixed dotted tail is not accepted by the strict parser
        assertTrue(HelperRediscover.addressKey("fd00::1", 8492) != HelperRediscover.addressKey("fd00::1", 8493))
        val probed = mutableListOf<String>()
        val rec = "unused"
        HelperRediscover.findVerifiedMove("fd00::1", 8492, "Office PC", rec,
            listOf(h("FD00:0:0:0:0:0:0:1", 8492), h("fd00::2", 8492), h("[fd00::2]", 8492), h("fd00:0:0:0:0:0:0:2", 8492)),
            verify = { host, _ -> probed += host; null })
        assertEquals(listOf("fd00::2"), probed)                               // saved endpoint dropped, three spellings of fd00::2 probed once
    }
}
