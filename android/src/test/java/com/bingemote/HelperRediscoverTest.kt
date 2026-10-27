package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** S11b regression: a spoofed helper responder - including one that copied the
 *  PC's discovery name - must never receive the saved helper token (and so never
 *  the VLC password either). The plan type has no token-carrying variant, so the
 *  only outcome a broadcast answer can earn is a notice; re-pair is the path. */
class HelperRediscoverTest {

    private fun helper(host: String, name: String = "Office PC", port: Int = 8492) =
        DiscoveredHelper(host, port, name)

    @Test fun `saved endpoint answering discovery needs no action`() {
        val plan = HelperRediscover.plan("192.168.1.50", 8492,
            listOf(helper("192.168.1.50"), helper("10.0.0.9")))
        assertEquals(HelperRediscover.Plan.SavedEndpointSeen, plan)
    }

    @Test fun `a new address earns only a notice - never a credential`() {
        val plan = HelperRediscover.plan("192.168.1.50", 8492, listOf(helper("10.0.0.9")))
        val moved = plan as HelperRediscover.Plan.MovedNotice
        assertEquals("10.0.0.9", moved.host)
        assertEquals(8492, moved.port)
        // The plan carries no token and no action that could send one.
    }

    @Test fun `a spoofed same-name responder still earns no trust`() {
        // Name collision: the attacker copied the PC's discovery name. Names are
        // labels - the answer is still only a notice, never the token.
        val found = listOf(helper("10.0.0.66", "Office PC"), helper("10.0.0.67", "Office PC"))
        val plan = HelperRediscover.plan("192.168.1.50", 8492, found)
        assertTrue(plan is HelperRediscover.Plan.MovedNotice)
    }

    @Test fun `nothing found keeps the saved endpoint`() {
        assertEquals(HelperRediscover.Plan.None,
            HelperRediscover.plan("192.168.1.50", 8492, emptyList()))
    }
}
