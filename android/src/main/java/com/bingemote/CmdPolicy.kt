package com.bingemote

/** The command-queue timing policy, kept tiny and pure so a unit test pins it down. */
object CmdPolicy {
    /** A queued play/pause/skip/seek tap older than this is dropped, not sent. */
    const val EXPIRY_MS = 2000L
    /** A queued subtitle command runs only if it was queued for the CURRENT subtitle request
     *  generation. Non-subtitle commands carry a sentinel and never reach this rule; subtitle
     *  commands must be queued with the current subtitleRequest (see RemoteState.fireSubtitle). */
    fun keepSubtitleCmd(cmdGeneration: Int, currentGeneration: Int): Boolean = cmdGeneration == currentGeneration
    /** While VLC is closed, it is re-checked every Nth poll instead of every poll. */
    const val CLOSED_POLL_EVERY = 4
}
