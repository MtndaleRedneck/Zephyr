package com.bingemote

import com.zephyr.remote.ZephyrClient
import com.zephyr.remote.StreamCoordinator

object StreamTarget {
    data class Target(val source: Source, val host: String, val port: Int, val credential: String, val pcId: String = "", val pinHex: String = "") {
        /** The whole Player tuple this target was captured with. */
        fun zephyr() = Store.ZephyrTarget(host, port, credential, pcId, pinHex)
        fun state(): com.zephyr.remote.StreamIdlePolicy.PlayerState =
            if (source == Source.ZEPHYR) com.zephyr.remote.StreamIdlePolicy.mapState(ZephyrClient.controlStatus(zephyr()).optString("state", ""))
            else com.zephyr.remote.StreamIdlePolicy.mapState(Vlc.status(host, port, credential).state)
    }
    fun capture(s: Store): Target = if (s.selectedSource == Source.ZEPHYR)
        s.zephyrTarget().let { t -> Target(Source.ZEPHYR, t?.host ?: throw java.io.IOException("Zephyr PC isn't paired yet."), t.port, t.token, t.pcId, t.pinHex) }   // host, port and token from one read
        else s.helperVlcSnapshot().let { Target(Source.VLC, it.endpoint.host, it.vlcPort, it.vlcPassword) }   // host, port, password from one locked read
    fun play(s: Store, url: String, subs: List<String>) = play(capture(s), url, subs)
    fun play(target: Target, url: String, subs: List<String>) {
        // Capture ownership even if the final response is lost. UNKNOWN never authorizes stop.
        val addedReceiver = StreamCoordinator.servedReceivers.add(target)
        val previousServed = StreamCoordinator.servedTarget
        StreamCoordinator.servedTarget = if (target.source == Source.ZEPHYR) StreamCoordinator.ServedTarget.ZEPHYR else StreamCoordinator.ServedTarget.VLC
        if (target.source == Source.ZEPHYR) {
            try { ZephyrClient.play(target.zephyr(), url, subs) }
            catch (e: ZephyrClient.ScreenWoke) {
                // Nothing started: undo only what THIS call added, so an earlier real stream keeps its watchdog state.
                if (addedReceiver) StreamCoordinator.servedReceivers.remove(target)
                StreamCoordinator.servedTarget = previousServed
                throw e
            }
        } else Vlc.playUri(target.host, target.port, target.credential, url, subs)
    }
}
