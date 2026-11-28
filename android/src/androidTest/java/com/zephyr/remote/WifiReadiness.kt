package com.zephyr.remote

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import java.net.Inet4Address
import java.net.InetAddress

/**
 * Precondition for the suites that need the phone stream server: the emulator must have a Wi-Fi network with an IPv4
 * address, the product's own selection (StreamService.findWifiIpv4). A bounded
 * poll, then a hard failure that prints what the platform reports, so a missing network is labelled as such instead of showing
 * up as a bare "server not ready". It changes no product code, timeout or assertion, and it is NOT called by the deliberate
 * no-network suite (StreamServiceNoWifiTest). Closes an evidence gap only; it does not explain why Wi-Fi was absent.
 */
object WifiReadiness {
    /** The product's own selection (StreamService.findWifiIpv4), so the precondition cannot drift from what the service does. */
    fun wifiIpv4(ctx: Context): InetAddress? =
        StreamService.findWifiIpv4(ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager)

    @Suppress("DEPRECATION")
    fun describe(ctx: Context): String {
        val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val active = cm.activeNetwork
        val sb = StringBuilder("activeNetwork=").append(active)
        for (n in cm.allNetworks) {
            val c = cm.getNetworkCapabilities(n)
            val lp = cm.getLinkProperties(n)
            sb.append("\n network=").append(n).append(if (n == active) " (active)" else "")
                .append(" wifi=").append(c?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI))
                .append(" cellular=").append(c?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR))
                .append(" validated=").append(c?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED))
                .append(" addresses=").append(lp?.linkAddresses?.map { it.address.hostAddress })
        }
        return sb.toString()
    }

    /** Hard RED if no active Wi-Fi IPv4 appears within [budgetMs]; no skip, no relaxed assertion. */
    fun require(ctx: Context, budgetMs: Long = 30_000) {
        val end = System.currentTimeMillis() + budgetMs
        while (System.currentTimeMillis() < end) {
            if (wifiIpv4(ctx) != null) return
            Thread.sleep(500)
        }
        if (wifiIpv4(ctx) != null) return
        throw AssertionError("PRECONDITION FAILED: no active Wi-Fi IPv4 network within $budgetMs ms (emulator network state, not a product assertion).\n" + describe(ctx))
    }
}
