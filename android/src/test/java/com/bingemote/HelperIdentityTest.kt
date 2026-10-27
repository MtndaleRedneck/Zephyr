package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Test

/** The phone only ever talks the Zephyr VLC Remote identity - its own discovery port
 *  (8491) and HTTP port (8492) - so a BingeMote install on the same PC is never
 *  discovered, paired with, or driven by this app. */
class HelperIdentityTest {
    @Test
    fun helperPortsAreZephyrOwn() {
        assertEquals(8492, HelperClient.DEFAULT_PORT)
        assertEquals(8491, HelperClient.DISCOVERY_PORT)
    }
}
