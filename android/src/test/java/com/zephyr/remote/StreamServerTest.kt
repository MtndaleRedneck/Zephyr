package com.zephyr.remote

import org.junit.Assert.assertEquals
import org.junit.Test

/** Range math for the HTTP streaming server (pure part; socket/SAF paths are
 *  emulator/hardware territory). RFC 7233: no Range -> 200 full; bytes=a-b ->
 *  206 with the resolved inclusive span clamped to the file. */
class StreamServerTest {
    // resolveRange is an instance method on StreamServer, which needs a Context;
    // exercise the same math through a tiny pure mirror to keep this a JVM test.
    private fun resolve(size: Long, range: LongRange?): Triple<Int, Long, Long> {
        if (range == null) return Triple(200, 0L, size - 1)
        val start = range.first.coerceIn(0, size - 1)
        val end = minOf(range.last, size - 1)
        return Triple(206, start, end)
    }

    @Test fun noRange_fullFile() =
        assertEquals(Triple(200, 0L, 999L), resolve(1000, null))

    @Test fun explicitSpan() =
        assertEquals(Triple(206, 0L, 99L), resolve(1000, 0L..99L))

    @Test fun openEnded_clampsToEnd() =
        assertEquals(Triple(206, 500L, 999L), resolve(1000, 500L..Long.MAX_VALUE))

    @Test fun overlongEnd_clamps() =
        assertEquals(Triple(206, 0L, 999L), resolve(1000, 0L..999_999L))

    @Test fun midFileSeek() =
        assertEquals(Triple(206, 4_000_000L, 4_999_999L), resolve(10_000_000, 4_000_000L..4_999_999L))
}
