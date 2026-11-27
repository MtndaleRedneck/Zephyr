package com.bingemote

/**
 * Decides when the "PC moved" notice may show. A durable address move is not a working session, so the notice waits for an
 * authenticated poll that succeeded against THAT moved endpoint (an older in-flight poll to another endpoint never confirms it),
 * and only within [pendingMs] of the move (a much later success must not announce an old move). It then stays [showMs] so it can be read.
 * Pure and clock-injected.
 */
class MovedNoticeGate(private val showMs: Long = 10_000L, private val pendingMs: Long = 60_000L) {
    private var pending: String? = null
    private var pendingKey: String? = null
    private var pendingAt = 0L
    private var until = 0L
    private var shown: String? = null

    /** [endpointKey] identifies the moved endpoint (address and token) that a confirming poll must have used. */
    @Synchronized fun onDurableMove(name: String, endpointKey: String, now: Long) { pending = name; pendingKey = endpointKey; pendingAt = now }

    /** Call on each successful authenticated poll with the endpoint key that poll used. Returns the notice text to show now, or null for none. */
    @Synchronized fun onAuthenticatedPoll(now: Long, polledKey: String): String? {
        if (pending != null && now - pendingAt > pendingMs) { pending = null; pendingKey = null }
        pending?.let { if (pendingKey == polledKey) { shown = it; pending = null; pendingKey = null; until = now + showMs } }
        if (shown != null && now > until) shown = null
        return shown
    }

    /** Source switch or a successful re-pair (a new key makes any earlier "PC moved" notice about the old PC wrong): drops a pending AND a showing notice. */
    @Synchronized fun clear() { pending = null; pendingKey = null; shown = null }
}
