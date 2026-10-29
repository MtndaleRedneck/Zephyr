package com.bingemote

import com.zephyr.remote.ZephyrClient
import com.zephyr.remote.StreamCoordinator

object StreamTarget {
    data class Target(val source: Source, val host: String, val port: Int, val credential: String) {
        override fun toString() = "Target(source=$source, host=$host, port=$port, credential=<redacted>)"
        fun state(): com.zephyr.remote.StreamIdlePolicy.PlayerState =
            if (source == Source.ZEPHYR) com.zephyr.remote.StreamIdlePolicy.mapState(ZephyrClient.controlStatus(host, port, credential).optString("state", ""))
            else com.zephyr.remote.StreamIdlePolicy.mapState(Vlc.status(host, port, credential).state)
    }
    fun capture(s: Store): Target = if (s.selectedSource == Source.ZEPHYR)
        Target(Source.ZEPHYR, s.zephyrEndpoint ?: throw java.io.IOException("Zephyr PC isn't paired yet."), s.zephyrPort, s.zephyrToken)
        else Target(Source.VLC, s.host, s.vlcPort, s.vlcPassword)
    fun play(s: Store, url: String, subs: List<String>) = play(capture(s), url, subs)
    fun play(target: Target, url: String, subs: List<String>) {
        // Capture ownership even if the final response is lost. UNKNOWN never authorizes stop.
        StreamCoordinator.servedReceivers.add(target)
        StreamCoordinator.servedTarget = if (target.source == Source.ZEPHYR) StreamCoordinator.ServedTarget.ZEPHYR else StreamCoordinator.ServedTarget.VLC
        if (target.source == Source.ZEPHYR) ZephyrClient.play(target.host, target.port, target.credential, url, subs)
        else Vlc.playUri(target.host, target.port, target.credential, url, subs)
    }
}
