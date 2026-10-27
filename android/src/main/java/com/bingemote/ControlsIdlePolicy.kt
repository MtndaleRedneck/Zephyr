package com.bingemote

/** Lock-screen controls idle stop (teardown 1b, settled: "stopSelf after a few
 *  minutes of nothing playing"). Paused is alive - an intentionally paused movie
 *  keeps its controls. Only two states end the service:
 *  - the player has not answered for the window (PC asleep/off for good), or
 *  - the player answers but has reported "stopped" for the whole window.
 *  A fresh boot can make elapsedRealtime smaller than the window: that only ever
 *  delays the stop, never triggers it early. */
class ControlsIdlePolicy(private val windowMs: Long = 5 * 60_000L) {

    private var lastSuccessMs = -1L
    private var stoppedSinceMs = -1L

    /** The player answered. True exactly when the controls service should stop. */
    fun onSuccess(nowMs: Long, state: String): Boolean {
        lastSuccessMs = nowMs
        if (state == "stopped") {
            if (stoppedSinceMs < 0) stoppedSinceMs = nowMs
            return nowMs - stoppedSinceMs >= windowMs
        }
        stoppedSinceMs = -1
        return false
    }

    /** The player did not answer. True after the window with no answer at all,
     *  once the session has already been marked inactive (failures >= 5). */
    fun onFailure(nowMs: Long, failures: Int): Boolean {
        if (failures < 5) return false
        val since = if (lastSuccessMs < 0) 0L else lastSuccessMs
        return nowMs - since >= windowMs
    }
}
