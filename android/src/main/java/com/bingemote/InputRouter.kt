package com.bingemote

import com.zephyr.remote.ZephyrClient
import java.io.IOException

/**
 * Source routing (protocol.md Source routing): input frames go ONLY to the
 * selected source - the phone's selector is the one arbitration point, the two
 * PC programs keep independent identities (no stand-down, no takeover, no
 * silent fallback). Helper path uses the rediscovered live address; the Zephyr
 * path uses the paired Zephyr Player identity.
 */
object InputRouter {

    

    private inline fun routed(s: Store, crossinline z: (Store.ZephyrTarget) -> Unit, crossinline h: () -> Unit) {
        if (s.selectedSource == Source.ZEPHYR) {
            // Never fall through to the helper: frames to the wrong PC target are
            // worse than a visible failure.
            // One whole-tuple read per frame; the client refuses it if the pairing changed before it is sent.
            val t = s.zephyrTarget() ?: throw IOException("Zephyr PC isn't paired yet - pair it in Settings > Connected Sources.")
            z(t)
        } else h()
    }

    fun move(remote: RemoteState, dx: Float, dy: Float) = routed(remote.store,
        { t -> ZephyrClient.inputMove(t, dx.toDouble(), dy.toDouble()) },
        { HelperClient.move(remote.liveHost, remote.livePort, remote.liveToken, dx, dy) })

    fun button(remote: RemoteState, button: String, down: Boolean) = routed(remote.store,
        { t -> ZephyrClient.inputButton(t, button, down) },
        { HelperClient.button(remote.liveHost, remote.livePort, remote.liveToken, button, down) })

    fun scroll(remote: RemoteState, dy: Float) = routed(remote.store,
        { t -> ZephyrClient.inputScroll(t, dy.toDouble()) },
        { HelperClient.scroll(remote.liveHost, remote.livePort, remote.liveToken, dy) })

    fun zoom(remote: RemoteState, delta: Float) = routed(remote.store,
        { t -> ZephyrClient.inputZoom(t, delta.toDouble()) },
        { HelperClient.zoom(remote.liveHost, remote.livePort, remote.liveToken, delta) })

    fun key(remote: RemoteState, key: String) = routed(remote.store,
        { t -> ZephyrClient.inputKey(t, key) },
        { HelperClient.key(remote.liveHost, remote.livePort, remote.liveToken, key) })

    fun typeText(remote: RemoteState, text: String) = routed(remote.store,
        { t -> ZephyrClient.inputType(t, text) },
        { HelperClient.typeText(remote.liveHost, remote.livePort, remote.liveToken, text) })
}
