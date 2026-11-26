package com.bingemote

import kotlinx.coroutines.channels.Channel

/**
 * The phone's tap queue: take / fold / drop decisions only, moved out of RemoteState unchanged so a JVM test can
 * drive it. Execution (try/catch around block, pairing-state updates) stays in RemoteState's worker.
 * Rules (same as before): a "sub" command runs only for the CURRENT subtitle generation; a burst of volume steps
 * folds into the newest; a tap older than CmdPolicy.EXPIRY_MS is dropped, except mute and sub.
 */
internal class CommandQueue(
    private val currentSubtitleGeneration: () -> Int,
    private val now: () -> Long = System::currentTimeMillis,
    private val onVolumeCoalesced: () -> Unit = {},
    private val onExpired: (Cmd) -> Unit = {},
) {
    class Cmd(val kind: String, val ts: Long, val subtitleGeneration: Int, val block: () -> Unit)

    private val channel = Channel<Cmd>(Channel.UNLIMITED)
    @Volatile private var pushback: Cmd? = null // one non-volume command held while volume folds

    fun trySend(c: Cmd) { channel.trySend(c) }

    /** Suspends until a command should run; drops and folds as above. */
    suspend fun next(): Cmd {
        while (true) {
            var c = pushback?.also { pushback = null } ?: channel.receive()
            if (c.kind == "sub" && !CmdPolicy.keepSubtitleCmd(c.subtitleGeneration, currentSubtitleGeneration())) continue
            if (c.kind == "volume") {
                while (true) {
                    val nxt = channel.tryReceive().getOrNull() ?: break
                    if (nxt.kind == "volume") { onVolumeCoalesced(); c = nxt }
                    else { pushback = nxt; break }
                }
            } else if (c.kind != "mute" && c.kind != "sub" && now() - c.ts > CmdPolicy.EXPIRY_MS) {
                onExpired(c)
                continue
            }
            return c
        }
    }
}
