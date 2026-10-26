package com.bingemote

import com.zephyr.remote.ZephyrClient

/**
 * Phone-origin playback goes to the SELECTED target only (protocol.md Source
 * routing): Zephyr PC gets ZephyrClient.play; VLC PC gets the same phone-served
 * URL through VLC's in_play. No fallthrough between targets.
 */
object StreamTarget {
    fun play(s: Store, url: String, subs: List<String>) {
        if (s.selectedSource == Source.ZEPHYR) {
            ZephyrClient.play(s.zephyrHost.ifBlank { s.host }, s.zephyrPort, s.zephyrToken, url, subs)
        } else {
            // VLC fetches the phone's URL over LAN; sidecar subtitles ride as
            // :sub-file item options (VLC 3.0.x httprequests.lua in_play).
            Vlc.playUri(s.host, s.vlcPort, s.vlcPassword, url, subs)
        }
    }
}
