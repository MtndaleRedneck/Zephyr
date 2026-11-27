package com.bingemote

/**
 * The address and token the screens show as the current pairing, as ONE immutable snapshot.
 * Every update is a re-read of the stored record, never a value carried from an earlier operation, and the read and the
 * write happen under one lock that is held for nothing else (no network, no main-thread wait). So a delayed or reordered
 * trigger cannot publish an older record: whichever trigger runs last reads the store after every commit that has
 * fired its own trigger. Not wired into any screen yet.
 */
internal class PairingView(private val read: () -> PairedCredential?) {
    class Snapshot(val host: String, val port: Int, val token: String)
    private val lock = Any()
    @Volatile var snapshot: Snapshot? = null
        private set

    /** Call after every successful save, forget or address update. */
    fun publishCurrent() {
        synchronized(lock) { snapshot = read()?.let { Snapshot(it.host, it.port, it.token) } }
    }
}
