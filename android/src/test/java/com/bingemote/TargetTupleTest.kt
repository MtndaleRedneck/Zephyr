package com.bingemote

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Whole-tuple identity: one differing part (token, address, either port, VLC password) is a different target. */
class TargetTupleTest {
    private fun snap(host: String = "10.0.0.5", port: Int = 8492, token: String = "t", vlcPort: Int = 8080, pw: String = "p") =
        Store.HelperVlcSnapshot(Store.HelperEndpoint(host, port, token), vlcPort, pw)

    @Test fun vlcSnapshotsMatchOnlyWhenEveryPartMatches() {
        val a = snap()
        assertTrue(a.sameTarget(snap()))
        assertFalse(a.sameTarget(null))
        assertFalse(a.sameTarget(snap(token = "t2")))
        assertFalse(a.sameTarget(snap(host = "10.0.0.6")))
        assertFalse(a.sameTarget(snap(port = 8493)))
        assertFalse(a.sameTarget(snap(vlcPort = 8081)))
        assertFalse(a.sameTarget(snap(pw = "p2")))   // same pairing token, new VLC password: not the listed target
    }

    @Test fun zephyrTargetsMatchOnlyWhenEveryPartMatches() {
        val a = Store.ZephyrTarget("10.0.0.5", 47823, "t")
        assertTrue(a.same(Store.ZephyrTarget("10.0.0.5", 47823, "t")))
        assertFalse(a.same(null))
        assertFalse(a.same(Store.ZephyrTarget("10.0.0.5", 47823, "t2")))
        assertFalse(a.same(Store.ZephyrTarget("10.0.0.5", 47824, "t")))   // same token, other port
        assertFalse(a.same(Store.ZephyrTarget("10.0.0.6", 47823, "t")))
        val full = Store.ZephyrTarget("10.0.0.5", 47823, "t", "a".repeat(32), "b".repeat(64))
        assertTrue(full.same(Store.ZephyrTarget("10.0.0.5", 47823, "t", "a".repeat(32), "b".repeat(64))))
        assertFalse(full.same(Store.ZephyrTarget("10.0.0.5", 47823, "t", "c".repeat(32), "b".repeat(64))))   // other local id
        assertFalse(full.same(Store.ZephyrTarget("10.0.0.5", 47823, "t", "a".repeat(32), "d".repeat(64))))   // other key
    }
}
