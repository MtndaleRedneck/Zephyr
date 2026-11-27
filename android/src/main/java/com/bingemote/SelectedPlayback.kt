package com.bingemote

import com.zephyr.remote.ZephyrClient

/** Source routing shared by background media controls. Never falls back to another player. */
internal class SelectedPlayback(private val store: Store) {
    private val source = store.selectedSource
    // Host, port and secret of the VLC target come from one locked read so a re-pair cannot mix an old host with a new password.
    private val snap: Store.HelperVlcSnapshot? = if (source == Source.ZEPHYR) null else store.helperVlcSnapshot()
    private val zephyr: Store.ZephyrTarget? = if (source == Source.ZEPHYR) store.zephyrTarget() else null   // the whole tuple, captured once
    private val target: Triple<String, Int, String> =
        snap?.let { Triple(it.endpoint.host, it.vlcPort, it.vlcPassword) } ?: Triple(zephyr?.host ?: "", zephyr?.port ?: 0, zephyr?.token ?: "")
    /** Pairing token of the PC this operation targets (null for Zephyr). A heal of a refusal must be bound to it. */
    val pcToken: String? get() = snap?.endpoint?.token
    private val host get() = target.first
    private val port get() = target.second
    private val secret get() = target.third
    private fun checkTarget() {
        check(store.selectedSource == source) { "Selected player changed" }
        check(source != Source.ZEPHYR || secret.isNotBlank()) { "Zephyr PC isn't paired" }
        check(source != Source.ZEPHYR || host.isNotBlank()) { "Zephyr PC isn't paired" }
    }
    fun status(): VlcStatus {
        checkTarget()
        if (source == Source.VLC) return Vlc.status(host, port, secret)
        val j = ZephyrClient.controlStatus(zephyr ?: throw java.io.IOException("Zephyr PC isn't paired"))
        return VlcStatus(j.optString("state", "stopped"), j.optString("title", ""),
            j.optLong("time"), j.optLong("length"), j.optInt("volume", 100).coerceIn(0,100)*256/100,
            j.optBoolean("fullscreen"), emptyList())
    }
    fun togglePause() {
        // VLC has a real toggle command: sending it must not wait on (or fail with) a status
        // read. Only Zephyr needs the current state, because its API has explicit pause/resume.
        if (source == Source.VLC) { checkTarget(); Vlc.togglePause(host, port, secret); return }
        val st = status(); checkTarget()
        if (st.playing) ZephyrClient.pause(zephyr!!)
        else ZephyrClient.resume(zephyr!!)
    }
    fun skip(delta: Int) {
        // VLC has a real relative seek: no status read to wait on or fail with.
        if (source == Source.VLC) { checkTarget(); Vlc.skip(host, port, secret, delta); return }
        val st = status(); checkTarget()
        // Length 0/unknown must not pin the target to 0 (a jump to the start).
        val next = (st.timeSec + delta).let { if (st.lengthSec > 0) it.coerceIn(0, st.lengthSec) else it.coerceAtLeast(0) }
        ZephyrClient.seek(zephyr!!, next)
    }
}
