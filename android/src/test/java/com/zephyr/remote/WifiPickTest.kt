package com.zephyr.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.net.InetAddress

/** Pure selection rule of StreamService.pickWifi (no Android framework). Does not exercise ConnectivityManager or a device. */
class WifiPickTest {
    private val wifiA: InetAddress = InetAddress.getByName("10.0.2.16")
    private val wifiB: InetAddress = InetAddress.getByName("192.168.1.20")
    // network id -> its Wi-Fi IPv4, or null when it is not Wi-Fi / has no IPv4 (cellular, IPv6-only, VPN-less other)
    private fun pick(active: Int?, all: List<Int>, m: Map<Int, InetAddress>, vpn: Set<Int> = emptySet()) =
        StreamService.pickWifi(active, all.toTypedArray(), { it.toLong() }, { m[it] }, { it in vpn })

    @Test fun activeWifiIsUsed() = assertEquals(wifiA, pick(100, listOf(100, 101), mapOf(100 to wifiA)))
    @Test fun cellularDefaultFallsBackToWifiWithIpv4() = assertEquals(wifiA, pick(101, listOf(101, 100), mapOf(100 to wifiA)))
    @Test fun activeWifiBeatsLowerHandleWifi() = assertEquals(wifiB, pick(105, listOf(100, 105), mapOf(100 to wifiA, 105 to wifiB)))
    @Test fun fallbackIsLowestHandleRegardlessOfListOrder() {
        val m = mapOf(105 to wifiB, 100 to wifiA)
        assertEquals(wifiA, pick(101, listOf(105, 100), m))
        assertEquals(wifiA, pick(101, listOf(100, 105), m))
    }
    @Test fun noWifiOrNoIpv4ReturnsNull() {
        assertNull(pick(101, listOf(101), emptyMap()))
        assertNull(pick(null, emptyList(), emptyMap()))
        assertNull(pick(null, listOf(100), emptyMap()))   // a Wi-Fi network whose only addresses are IPv6 maps to null
    }
    @Test fun fallbackSkipsVpnEvenWithLowerHandle() = assertEquals(wifiB, pick(101, listOf(100, 105), mapOf(100 to wifiA, 105 to wifiB), setOf(100)))
    @Test fun fallbackWithOnlyVpnReturnsNull() = assertNull(pick(101, listOf(100), mapOf(100 to wifiA), setOf(100)))
    @Test fun activeVpnWifiKeepsOldBehavior() = assertEquals(wifiA, pick(100, listOf(100), mapOf(100 to wifiA), setOf(100)))
    @Test fun nullActiveStillFindsWifi() = assertEquals(wifiA, pick(null, listOf(100), mapOf(100 to wifiA)))
}
