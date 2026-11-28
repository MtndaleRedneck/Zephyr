package com.zephyr.remote

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat

/**
 * Foreground service that keeps the phone awake and serving while the Player streams.
 *
 * Type RESOLVED: dataSync (serving files over the network) per
 * https://developer.android.com/develop/background-work/services/fgs/service-types
 *
 * Verified constraints (https://developer.android.com/develop/background-work/services/fgs/timeout):
 *  - dataSync services get 6 HOURS per 24h (Android 15+), shared across the app's
 *    dataSync services; the clock resets when the app comes to the foreground.
 *    Full-length movies fit, but marathons can exhaust it - onTimeout must stop
 *    cleanly, and a start with the budget exhausted throws
 *    ForegroundServiceStartNotAllowedException (caller must catch and say so).
 *  - Started only from direct user interaction (tap in the Movies screen), so the
 *    app is foreground and the full budget is available - the recommended pattern.
 * Still open: WIFI lock behavior + 2-hour screen-off soak test gates any claim.
 */
class StreamService : Service() {

    inner class Binder : android.os.Binder() {
        fun server(): StreamServer? = this@StreamService.server
        /** True when THIS instance's foreground start was refused; the instance is stopping and must not serve. */
        fun refused(): Boolean = this@StreamService.fgRefused
    }
    private val binder = Binder()

    private var server: StreamServer? = null
    /** Instance-local (no static state to reset): the foreground start was refused in onCreate. */
    @Volatile private var fgRefused = false
    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null
    @Volatile private var watchdogStop = false
    private var watchdog: Thread? = null

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Streaming", NotificationManager.IMPORTANCE_LOW)
        )
        val stopIntent = PendingIntent.getService(
            this, 0,
            Intent(this, StreamService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE
        )
        val notification: Notification = NotificationCompat.Builder(this, CHANNEL)
            .setContentTitle("Zephyr") // placeholder name pending final branding
            .setContentText("Serving a movie to your PC")
            .setSmallIcon(android.R.drawable.ic_media_play) // placeholder icon until branding lands
            .addAction(0, "Stop", stopIntent)
            .setOngoing(true)
            .build()
        try {
            if (com.bingemote.BuildConfig.DEBUG && testForegroundRefusal) throw IllegalStateException("test: foreground start refused")
            startForeground(NOTIF_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) // type under verification
        } catch (e: Exception) { android.util.Log.e("ZephyrStream", "startForeground refused, stopping service", e); fgRefused = true; ev("created-refused"); stopSelf() } // foreground start refused: end the service instead of crashing
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // A stateless restart must not stand: when the process was killed the streams
        // died with it, and a START_STICKY rebuild would hold the wake + Wi-Fi locks
        // with servedAny false and no target, so the watchdog could never arm (N1).
        // NOT_STICKY keeps every stop rule for REAL streams exactly as settled; it
        // only ends the zero-state zombie.
        ev("start-command")
        if (intent == null) { stopSelf(); return START_NOT_STICKY }
        // Refused foreground start: never build the server, locks or watchdog for this instance.
        if (fgRefused) { ev("start-guard-refused"); stopSelf(); return START_NOT_STICKY }
        if (intent.action == ACTION_STOP || server != null) {
            if (intent.action == ACTION_STOP) stopSelf()
            return START_NOT_STICKY
        }
        val wifiAddr = wifiAddress() ?: run { stopSelf(); return START_NOT_STICKY } // bind Wi-Fi only
        val srv = StreamServer(this, wifiAddr)
        // Movie + sidecar subtitle URIs arrive via the activity binder/intent extras
        // (hooked up when the Movies screen wiring lands); subtitles get /sub/<n> URLs.
        StreamCoordinator.servedReceivers.clear()
        StreamCoordinator.servedTarget = null
        try { srv.start() } catch (e: Exception) { runCatching { srv.stop() }; stopSelf(); return START_NOT_STICKY }
        server = srv
        ev("server-built")

        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        wifiLock = if (Build.VERSION.SDK_INT >= 29)
            wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "zephyr:stream")
        else
            @Suppress("DEPRECATION") wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "zephyr:stream")
        wifiLock?.setReferenceCounted(false)
        wifiLock?.acquire()
        wakeLock = (getSystemService(Context.POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "zephyr:stream")
            .also { it.acquire(6 * 60 * 60 * 1000L) } // safety net; stop paths release it sooner
        ev("locks-acquired")
        startWatchdog(srv)
        ev("watchdog-started")
        return START_NOT_STICKY
    }

    /**
     * Auto-stop when the stream is genuinely done (teardown C1, settled):
     * the SAME 5-minute rule covers the Zephyr AND the VLC target. The watchdog
     * polls the player the stream was actually handed to (StreamCoordinator.servedTarget,
     * captured at play time) - never the mutable UI source selection, so flipping
     * sources mid-stream can't wrongly stop or strand a stream. Decision logic lives
     * in the pure-JVM StreamIdlePolicy (unit-tested): armed after the first served
     * stream, an open connection is always life, playing/paused/unknown/unreachable
     * all cancel, Wi-Fi loss alone never stops the service.
     */
    private fun startWatchdog(srv: StreamServer) {
        watchdogStop = false
        watchdog = Thread {
            val policy = StreamIdlePolicy()
            while (!watchdogStop) {
                try { Thread.sleep(60_000) } catch (e: InterruptedException) { break }
                if (watchdogStop) break
                val state = try { servedTargetState() }
                catch (e: Exception) { StreamIdlePolicy.PlayerState.UNKNOWN } // unreachable cancels
                if (policy.tick(srv.servedAny, srv.openStreams.get(), state)) { stopSelf(); break }
            }
        }.apply { isDaemon = true; name = "stream-watchdog"; start() }
    }

    /** Status of the player the stream was handed to (settled C1 covers both paths).
     *  No handoff yet = UNKNOWN = never auto-stop. */
    private fun servedTargetState(): StreamIdlePolicy.PlayerState = StreamCoordinator.receiverState()

    /** Android 15+: dataSync budget (6h/24h) exhausted. Stop serving cleanly. */
    override fun onTimeout(startId: Int, fgsType: Int) {
        stopSelf()
    }

    override fun onDestroy() {
        watchdogStop = true
        watchdog?.interrupt()
        server?.stop()
        StreamCoordinator.servedReceivers.clear()
        StreamCoordinator.servedTarget = null
        runCatching { wifiLock?.release() }
        runCatching { wakeLock?.release() }
        // StreamServer drains owned provider/copy workers before deleting its cache.
        ev("destroyed")
        super.onDestroy()
    }

    private fun wifiAddress(): java.net.InetAddress? =
        findWifiIpv4(getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager)

    companion object {
        /**
         * IPv4 address of a connected Wi-Fi network, for binding the stream server to the Wi-Fi interface only. The default (active)
         * network is used when it is Wi-Fi, exactly as before. When Android keeps another network (e.g. cellular) as the default
         * because the Wi-Fi has no validated internet (a LAN-only Wi-Fi), the first Wi-Fi network that has an IPv4 address
         * is used instead; the bind is still to that one Wi-Fi address, never a wildcard.
         */
        @Suppress("DEPRECATION")
        internal fun findWifiIpv4(cm: ConnectivityManager): java.net.InetAddress? {
            fun v4(n: android.net.Network): java.net.InetAddress? {
                val caps = cm.getNetworkCapabilities(n) ?: return null
                if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return null
                return cm.getLinkProperties(n)?.linkAddresses?.map { it.address }?.firstOrNull { it is java.net.Inet4Address }
            }
            fun vpn(n: android.net.Network): Boolean = cm.getNetworkCapabilities(n)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true
            return pickWifi(cm.activeNetwork, cm.allNetworks, { it.networkHandle }, ::v4, ::vpn)
        }

        /** Selection rule, framework-free so plain unit tests cover it: the active network's Wi-Fi address first; otherwise the Wi-Fi
         *  address of the non-VPN candidate with the lowest [order] key (a fixed order, whatever order the platform lists networks in). */
        internal fun <N> pickWifi(active: N?, all: Array<N>, order: (N) -> Long, wifiV4: (N) -> java.net.InetAddress?, isVpn: (N) -> Boolean): java.net.InetAddress? {
            if (active != null) { val a = wifiV4(active); if (a != null) return a }
            var best: java.net.InetAddress? = null
            var bestKey = 0L
            for (n in all) {
                if (isVpn(n)) continue   // fallback only: never choose a tunnel by lowest handle; the active path is unchanged
                val a = wifiV4(n) ?: continue
                val k = order(n)
                if (best == null || k < bestKey) { best = a; bestKey = k }
            }
            return best
        }
        const val CHANNEL = "stream"
        const val NOTIF_ID = 4101
        const val ACTION_STOP = "com.zephyr.remote.STOP_STREAM"
        /** Debug-build test seam ONLY (BuildConfig.DEBUG gate at the use): the instrumented test sets and resets it. Not production state. */
        @Volatile internal var testForegroundRefusal = false
        /** Debug-build test seam ONLY: lifecycle events so a test can prove what a refused instance did NOT build. */
        internal val testEvents = java.util.concurrent.CopyOnWriteArrayList<String>()
        internal fun ev(s: String) { if (com.bingemote.BuildConfig.DEBUG) testEvents.add(s) }
    }
}
