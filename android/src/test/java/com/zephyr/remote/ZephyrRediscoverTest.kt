package com.zephyr.remote

import com.bingemote.DiscoveredHelper
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** S11 regression: the rediscovery plan has NO token-carrying action, so a
 *  spoofed broadcast answer can never receive the saved bearer token. */
class ZephyrRediscoverTest {

    @Test fun spoofedSameNameCandidateReceivesNoHttpRequestAtAll() {
        // HTTP-level proof: the spoof is a real server that answered discovery with
        // the saved PC's name. run() takes no token and no client, so it cannot
        // send a request - assert none arrived (no Authorization header can leak),
        // and the plan is only a notice (no save-host action exists to mutate the
        // saved endpoint).
        val spoof = MockWebServer(); spoof.start()
        try {
            val plan = ZephyrRediscover.run("192.168.1.50", 47823, "Office PC") {
                listOf(DiscoveredHelper("127.0.0.1", spoof.port, "Office PC"))
            }
            assertTrue(plan is ZephyrRediscover.Plan.MovedNotice)
            assertEquals(0, spoof.requestCount)
        } finally {
            spoof.shutdown()
        }
    }

    @Test fun spoofedSameNameCandidateGetsNoTokenOnlyANotice() {
        // An attacker answering the broadcast with the saved PC's name:
        val spoof = DiscoveredHelper("192.168.1.666".replace("666", "50"), 47823, "Living Room PC")
        val plan = ZephyrRediscover.plan("192.168.1.10", 47823, "Living Room PC", listOf(spoof))
        // The ONLY outcome is a user-facing notice - the plan carries the candidate
        // for display, and no plan variant carries or requests the token.
        assertTrue(plan is ZephyrRediscover.Plan.MovedNotice)
        assertEquals("192.168.1.50", (plan as ZephyrRediscover.Plan.MovedNotice).host)
    }

    @Test fun savedEndpointSeenNeedsNoAction() {
        val saved = DiscoveredHelper("192.168.1.10", 47823, "Living Room PC")
        assertEquals(ZephyrRediscover.Plan.SavedEndpointSeen,
            ZephyrRediscover.plan("192.168.1.10", 47823, "Living Room PC", listOf(saved)))
    }

    @Test fun unknownNamesYieldNothing() {
        val stranger = DiscoveredHelper("192.168.1.99", 47823, "Not Mine")
        assertEquals(ZephyrRediscover.Plan.None,
            ZephyrRediscover.plan("192.168.1.10", 47823, "Living Room PC", listOf(stranger)))
        // No saved name: nothing is ever acted on.
        assertEquals(ZephyrRediscover.Plan.None,
            ZephyrRediscover.plan("192.168.1.10", 47823, "", listOf(stranger)))
    }
}
