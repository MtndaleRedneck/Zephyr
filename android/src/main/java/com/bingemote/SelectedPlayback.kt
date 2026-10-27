package com.bingemote

import com.zephyr.remote.ZephyrClient

/** Source routing shared by background media controls. Never falls back to another player. */
internal class SelectedPlayback(private val store: Store) {
    private val source = store.selectedSource
    private val host = if (source == Source.ZEPHYR) store.zephyrHost.ifBlank { store.host } else store.host
    private val port = if (source == Source.ZEPHYR) store.zephyrPort else store.vlcPort
    private val secret = if (source == Source.ZEPHYR) store.zephyrToken else store.vlcPassword
    private fun checkTarget() {
        check(store.selectedSource == source) { "Selected player changed" }
        check(source != Source.ZEPHYR || secret.isNotBlank()) { "Zephyr PC isn't paired" }
    }
    fun status(): VlcStatus {
        checkTarget()
        if (source == Source.VLC) return Vlc.status(host, port, secret)
        val j = ZephyrClient.controlStatus(host, port, secret)
        return VlcStatus(j.optString("state", "stopped"), j.optString("title", ""),
            j.optLong("time"), j.optLong("length"), j.optInt("volume", 100).coerceIn(0,100)*256/100,
            j.optBoolean("fullscreen"), emptyList())
    }
    fun togglePause() {
        val st = status(); checkTarget()
        if (source == Source.VLC) Vlc.togglePause(host, port, secret)
        else if (st.playing) ZephyrClient.pause(host, port, secret)
        else ZephyrClient.resume(host, port, secret)
    }
    fun skip(delta: Int) {
        val st = status(); checkTarget()
        // Length 0/unknown must not pin the target to 0 (a jump to the start).
        val next = (st.timeSec + delta).let { if (st.lengthSec > 0) it.coerceIn(0, st.lengthSec) else it.coerceAtLeast(0) }
        if (source == Source.ZEPHYR) ZephyrClient.seek(host, port, secret, next)
        else Vlc.seekTo(host, port, secret, next)
    }
}
