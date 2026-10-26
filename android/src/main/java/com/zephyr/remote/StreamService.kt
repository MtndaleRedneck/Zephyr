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
    }
    private val binder = Binder()

    private var server: StreamServer? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null

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
        startForeground(NOTIF_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) // type under verification
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP || server != null) {
            if (intent?.action == ACTION_STOP) stopSelf()
            return START_STICKY
        }
        val wifiAddr = wifiAddress() ?: run { stopSelf(); return START_NOT_STICKY } // bind Wi-Fi only
        val srv = StreamServer(this, wifiAddr)
        // Movie + sidecar subtitle URIs arrive via the activity binder/intent extras
        // (hooked up when the Movies screen wiring lands); subtitles get /sub/<n> URLs.
        srv.start()
        server = srv

        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        wifiLock = if (Build.VERSION.SDK_INT >= 29)
            wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "zephyr:stream")
        else
            @Suppress("DEPRECATION") wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "zephyr:stream")
        wifiLock?.setReferenceCounted(false)
        wifiLock?.acquire()
        wakeLock = (getSystemService(Context.POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "zephyr:stream")
            .also { it.acquire() }
        return START_STICKY
    }

    /** Android 15+: dataSync budget (6h/24h) exhausted. Stop serving cleanly. */
    override fun onTimeout(startId: Int, fgsType: Int) {
        stopSelf()
    }

    override fun onDestroy() {
        server?.stop()
        runCatching { wifiLock?.release() }
        runCatching { wakeLock?.release() }
        super.onDestroy()
    }

    private fun wifiAddress(): java.net.InetAddress? {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val network = cm.activeNetwork ?: return null
        val caps = cm.getNetworkCapabilities(network) ?: return null
        if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return null
        return cm.getLinkProperties(network)?.linkAddresses
            ?.map { it.address }
            ?.firstOrNull { it is java.net.Inet4Address }
    }

    companion object {
        const val CHANNEL = "stream"
        const val NOTIF_ID = 4101
        const val ACTION_STOP = "com.zephyr.remote.STOP_STREAM"
    }
}
