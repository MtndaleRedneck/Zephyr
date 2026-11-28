package com.bingemote

/**
 * One address-follow attempt for the remembered Zephyr Player, as plain functions over injected calls so JVM tests can drive it (same shape as [HelperMove]).
 * Order: a pin-authenticated handshake to the SAVED endpoint first (no application bytes, no token). Otherwise the discovered Players are
 * handshake-checked against the STORED key; only a key match can move the stored address. A discovery reply is never proof that the saved
 * endpoint is alive (anyone on the LAN can send one); the saved address in the list is ignored. The token never goes to a discovered host.
 * After a move the fresh stored record is re-read and returned, never values carried from earlier steps.
 * [Outcome.Moved.committed] only says the store reported the commit as successful; it is not a power-loss claim.
 */
internal object ZephyrMove {
    /** One read of the stored record: the whole attempt works from this snapshot. */
    class Snapshot(val credential: ZephyrCredential, val raw: String, val name: String)

    sealed interface Commit {
        /** Nothing written (stale snapshot, key changed, busy or unhealthy store, or the read-back was not exactly the snapshot with only host and port replaced). */
        object Refused : Commit
        /** A write was attempted and the result could not be verified; the store is latched and the phone shows pair again. */
        object Blocked : Commit
        /** [credential] is the freshly re-read stored record, proven equal to the snapshot with only host/port replaced. */
        class Written(val credential: ZephyrCredential, val committed: Boolean) : Commit
    }

    /** What the attempt needs from the store. [commit] is the only writer. */
    interface Port {
        fun snapshot(): Snapshot?
        fun commit(v: SecureHttp.VerifiedAddress): Commit
    }

    sealed interface Outcome {
        object NotPaired : Outcome
        /** The pinned handshake of the saved endpoint matched: nothing to move. A discovery listing alone never produces this. */
        object SavedEndpointSeen : Outcome
        /** No acceptable discovered Player proved the stored key. [sameNameSeen] is the label of a same-name Player at another address (a label, not identity): the phone only says to pair again. */
        class NoneVerified(val sameNameSeen: String?) : Outcome
        class Moved(val credential: ZephyrCredential, val pcName: String, val committed: Boolean) : Outcome
        object Refused : Outcome
        object Blocked : Outcome
    }

    /** Handshake only (no application bytes, no token) against the key stored in [c]. A mismatch never marks the saved PC as key-changed. */
    fun handshakeMatches(c: ZephyrCredential, host: String, port: Int): Boolean =
        SecureHttp.probe(PinPolicy(mapOf(c.pcId to PairedCredential.pinBytes(c.pinHex))), c.pcId, host, port) == SecureHttp.ProbeResult.MATCH

    /**
     * [ensureActive] throws when the caller was cancelled. It is checked after the blocking probe and BEFORE the store is touched, so a cancel seen
     * before the write leaves the store unchanged and raises nothing. A cancel that arrives once the atomic write has started cannot undo it: the
     * stored record is wholly the old or wholly the new one, and the caller must not report it as a move.
     */
    fun attempt(
        port: Port,
        pingSaved: (ZephyrCredential) -> Boolean = { c -> handshakeMatches(c, c.host, c.port) },
        discover: () -> List<DiscoveredHelper>,
        verify: (ZephyrCredential, String, Int) -> Boolean = { c, h, p -> handshakeMatches(c, h, p) },
        addressOk: (String) -> Boolean = HelperRediscover::isPrivateLiteral,
        ensureActive: () -> Unit = {},
    ): Outcome {
        val snap = port.snapshot() ?: return Outcome.NotPaired
        val c = snap.credential
        if (pingSaved(c)) return Outcome.SavedEndpointSeen
        ensureActive()
        val found = discover()
        ensureActive()   // a cancel during the (blocking) discovery stops before any candidate is probed
        val v = HelperRediscover.findVerifiedMove(c.host, c.port, snap.name, snap.raw, found, addressOk) { h, p ->
            if (verify(c, h, p)) SecureHttp.VerifiedAddress(c.pcId, h, p, snap.raw) else null
        }
        ensureActive()   // after the blocking probes, before the store is touched
        if (v == null) {
            val savedKey = HelperRediscover.addressKey(c.host, c.port)
            val same = if (snap.name.isBlank()) null
                else found.firstOrNull { it.name.equals(snap.name, ignoreCase = true) && HelperRediscover.addressKey(it.host, it.port) != savedKey }?.name
            return Outcome.NoneVerified(same)
        }
        return when (val r = port.commit(v)) {
            Commit.Refused -> Outcome.Refused
            Commit.Blocked -> Outcome.Blocked
            is Commit.Written -> Outcome.Moved(r.credential, snap.name, r.committed)
        }
    }
}
