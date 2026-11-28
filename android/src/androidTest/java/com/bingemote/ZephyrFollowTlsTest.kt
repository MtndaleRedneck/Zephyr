package com.bingemote

import androidx.test.runner.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Real TLS, real handshake probes, the real Store writer: a fake Player at address A is stopped and a Player with the SAME key answers at B (loopback, a
 * different port, so the private-address filter is relaxed for the test only); the phone follows. The same fake with a stored key that is NOT the fake's does not follow.
 * Not a phone-on-two-networks proof.
 */
@RunWith(AndroidJUnit4::class)
class ZephyrFollowTlsTest {
    private val ctx get() = InstrumentationRegistry.getInstrumentation().targetContext
    @After fun cleanup() { ZephyrTestSeed.cleanup(ctx) }

    @Test fun theSameKeyAtANewAddressIsFollowedAndTheTokenStaysTheSame() {
        val a = TlsFakePlayer.server(); val b = TlsFakePlayer.server()
        try {
            val store = Store(ctx); ZephyrTestSeed.clear(store)
            ZephyrTestSeed.seed(store, "127.0.0.1", a.port, name = "Office PC")
            val before = store.zephyrTarget()!!
            a.shutdown()
            val out = ZephyrMove.attempt(store.zephyrMovePort(), discover = { listOf(DiscoveredHelper("127.0.0.1", b.port, "Office PC")) }, addressOk = { true })
            out as ZephyrMove.Outcome.Moved
            val now = Store(ctx).zephyrTarget()!!        // a second Store instance sees the shared, republished record
            assertEquals(b.port, now.port)
            assertEquals(listOf(before.pcId, before.pinHex, before.token), listOf(now.pcId, now.pinHex, now.token))
            assertTrue(out.committed)
        } finally { runCatching { b.shutdown() }; runCatching { a.shutdown() } }
    }

    @Test fun aDifferentKeyAtTheNewAddressIsNotFollowed() {
        val a = TlsFakePlayer.server(); val b = TlsFakePlayer.server()
        try {
            val store = Store(ctx); ZephyrTestSeed.clear(store)
            ZephyrTestSeed.seed(store, "127.0.0.1", a.port, pinHex = "00".repeat(32), name = "Office PC")   // stored key is not the fake's key
            val before = store.zephyrTarget()!!
            a.shutdown()
            val out = ZephyrMove.attempt(store.zephyrMovePort(), discover = { listOf(DiscoveredHelper("127.0.0.1", b.port, "Office PC")) }, addressOk = { true })
            assertTrue(out is ZephyrMove.Outcome.NoneVerified)
            assertTrue(before.same(store.zephyrTarget()))
            assertTrue(!KeyChangeState.isChanged(before.pcId))
        } finally { runCatching { b.shutdown() }; runCatching { a.shutdown() } }
    }
}
