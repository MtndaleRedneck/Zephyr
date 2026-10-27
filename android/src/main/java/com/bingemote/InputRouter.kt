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

    private fun zephyrReady(s: Store) = s.zephyrToken.isNotBlank()

    private inline fun routed(s: Store, crossinline z: () -> Unit, crossinline h: () -> Unit) {
        if (s.selectedSource == Source.ZEPHYR) {
            // Never fall through to the helper: frames to the wrong PC target are
            // worse than a visible failure.
            if (!zephyrReady(s)) throw IOException("Zephyr PC isn't paired yet - pair it in Settings > Connected Sources.")
            z()
        } else h()
    }

    fun move(remote: RemoteState, dx: Float, dy: Float) = routed(remote.store,
        { ZephyrClient.inputMove(remote.store.zephyrHost, remote.store.zephyrPort, remote.store.zephyrToken, dx.toDouble(), dy.toDouble()) },
        { HelperClient.move(remote.liveHost, remote.livePort, remote.liveToken, dx, dy) })

    fun button(remote: RemoteState, button: String, down: Boolean) = routed(remote.store,
        { ZephyrClient.inputButton(remote.store.zephyrHost, remote.store.zephyrPort, remote.store.zephyrToken, button, down) },
        { HelperClient.button(remote.liveHost, remote.livePort, remote.liveToken, button, down) })

    fun scroll(remote: RemoteState, dy: Float) = routed(remote.store,
        { ZephyrClient.inputScroll(remote.store.zephyrHost, remote.store.zephyrPort, remote.store.zephyrToken, dy.toDouble()) },
        { HelperClient.scroll(remote.liveHost, remote.livePort, remote.liveToken, dy) })

    fun zoom(remote: RemoteState, delta: Float) = routed(remote.store,
        { ZephyrClient.inputZoom(remote.store.zephyrHost, remote.store.zephyrPort, remote.store.zephyrToken, delta.toDouble()) },
        { HelperClient.zoom(remote.liveHost, remote.livePort, remote.liveToken, delta) })

    fun key(remote: RemoteState, key: String) = routed(remote.store,
        { ZephyrClient.inputKey(remote.store.zephyrHost, remote.store.zephyrPort, remote.store.zephyrToken, key) },
        { HelperClient.key(remote.liveHost, remote.livePort, remote.liveToken, key) })

    fun typeText(remote: RemoteState, text: String) = routed(remote.store,
        { ZephyrClient.inputType(remote.store.zephyrHost, remote.store.zephyrPort, remote.store.zephyrToken, text) },
        { HelperClient.typeText(remote.liveHost, remote.livePort, remote.liveToken, text) })
}
