package com.zephyr.remote

/**
 * Pure-JVM decision core for the stream auto-stop (teardown C1, settled):
 * the SAME 5-minute rule covers the Zephyr AND the VLC target - the watchdog
 * polls whichever player the stream was actually handed to.
 *
 *  - never acts before the first served stream;
 *  - an open connection is always life (a long pause holds its socket);
 *  - stops only after 5 continuous idle ticks with the target player in
 *    Stopped/Ended; playing, paused, unknown and unreachable all cancel.
 */
class StreamIdlePolicy {

    enum class PlayerState { PLAYING, PAUSED, STOPPED_OR_ENDED, UNKNOWN }

    private var stoppedStreak = 0

    /** One watchdog tick (~60s). True exactly when the service should stop. */
    fun tick(servedAny: Boolean, openStreams: Int, playerState: PlayerState): Boolean {
        if (!servedAny) return false
        if (openStreams > 0) { stoppedStreak = 0; return false }
        return if (playerState == PlayerState.STOPPED_OR_ENDED) {
            ++stoppedStreak >= 5
        } else {
            stoppedStreak = 0
            false
        }
    }

    companion object {
        /** Both players speak the same states (VLC status and Zephyr /control/status);
         *  anything blank or unrecognized is UNKNOWN, which cancels the countdown. */
        fun mapState(raw: String?): PlayerState = when (raw?.lowercase()) {
            "playing" -> PlayerState.PLAYING
            "paused" -> PlayerState.PAUSED
            "stopped", "ended" -> PlayerState.STOPPED_OR_ENDED
            else -> PlayerState.UNKNOWN
        }
    }
}
