package com.bingemote

/**
 * One rediscovery attempt for the remembered helper PC, as plain functions over injected calls so JVM tests can drive it.
 * Order: the saved endpoint is tried first (pinned, token only to the saved endpoint). Otherwise the discovered helpers (a listing of the saved address itself is ignored) are
 * handshake-checked against the STORED key with no token and no application bytes; only a key match can move the stored address.
 * After a move the fresh stored record is re-read and returned, never values carried from earlier steps.
 * Not a statement about power-loss durability: [Outcome.Moved.committed] only says the store reported the commit as successful.
 */
internal object HelperMove {
    sealed interface Outcome {
        /** No usable secure pairing is stored. */
        object NotPaired : Outcome
        /** The pinned check of the saved endpoint answered: nothing to move. A discovery listing alone never produces this. */
        object SavedEndpointSeen : Outcome
        /** No acceptable discovered helper proved the stored key. */
        object NoneVerified : Outcome
        /** The address moved (or was already there). [credential] is the fresh stored record. [committed] is false when the commit was not confirmed but the record re-read healthy and usable. */
        class Moved(val credential: PairedCredential, val pcName: String, val committed: Boolean) : Outcome
        /** A move was verified but nothing was written (stale snapshot, key changed, busy or unhealthy store). */
        object Refused : Outcome
        /** A write was attempted and the result could not be verified; the store is latched and the phone shows pair again. */
        object Blocked : Outcome
    }

    fun attempt(
        store: SecurePairStore,
        pingSaved: (PairedCredential) -> Boolean,
        discover: () -> List<DiscoveredHelper>,
        verify: (String, String, Int) -> SecureHttp.VerifiedAddress? = { record, h, p -> SecureHttp.verify(record, h, p) },
        addressOk: (String) -> Boolean = HelperRediscover::isPrivateLiteral,
    ): Outcome {
        // One read of the stored record is the snapshot for this whole attempt.
        val raw = try { store.credential } catch (e: Exception) { return Outcome.NotPaired }
        val snap = (SecurePairSave.load(store) as? PairedCredential.State.Secure)?.credential ?: return Outcome.NotPaired
        if (PairedCredential.decode(raw)?.encode() != raw || snap.encode() != raw) return Outcome.NotPaired
        val name = try { store.pcName } catch (e: Exception) { "" }
        if (pingSaved(snap)) return Outcome.SavedEndpointSeen
        val found = discover()
        // An unauthenticated discovery reply carrying the saved address proves nothing (anyone on the LAN can send it): only the
        // pinned pingSaved above proves the saved endpoint is alive. The candidate search already drops the saved address.
        val v = HelperRediscover.findVerifiedMove(snap.host, snap.port, name, raw, found, addressOk) { h, p -> verify(raw, h, p) }
            ?: return Outcome.NoneVerified
        val r = SecurePairSave.updateAddress(store, v)
        return when (r) {
            SecurePairSave.MoveResult.REFUSED -> Outcome.Refused
            SecurePairSave.MoveResult.BLOCKED -> Outcome.Blocked
            else -> {
                val fresh = (SecurePairSave.load(store) as? PairedCredential.State.Secure)?.credential
                // committed is true only for COMMITTED. UNCHANGED and SESSION_USABLE moved nothing, so they never raise a "moved" notice.
                if (fresh == null) Outcome.Blocked else Outcome.Moved(fresh, name, committed = r == SecurePairSave.MoveResult.COMMITTED)
            }
        }
    }
}
