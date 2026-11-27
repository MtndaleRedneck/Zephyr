package com.bingemote

/** What a 401 from VLC means and the ONE automatic thing the phone may do about it. */
object VlcAuthRepair {
    @Volatile private var healing = false

    /**
     * The only automatic heal for a refused password: the one-time adoption fetch from the
     * PC. The phone NEVER pushes its stored password at the PC on a 401 - a refusal can be
     * an intentional change made on the PC, and pushing would silently revert it. Returns
     * true when a fresh password was adopted and stored; false when the grant is spent, the
     * helper is too old, or the PC cannot be reached - the caller then surfaces the refusal.
     */
    /**
     * Writes the adopted VLC password only if the stored pairing is still the PC it was fetched from (same token; a move keeps the token,
     * a re-pair replaces it). The check and the write run under the pairing store's write lock, so a re-pair cannot land between them.
     * On a different or unreadable pairing nothing is written and a leftover request id is cleared only if it is still ours.
     * No network and no suspension under the lock. Lock order: heal monitor, then write lock; the pairing paths never take the heal monitor.
     */
    internal fun storeAdopted(ps: SecurePairStore, token: String, pw: String, reqId: String, getReq: () -> String?, setReq: (String?) -> Unit): Boolean =
        SecurePairSave.writerSection(ps) {   // writes the tuple's VLC password: published as one whole tuple on exit
            val cur = try { (SecurePairSave.load(ps) as? PairedCredential.State.Secure)?.credential } catch (e: Exception) { null }
            if (cur == null || cur.token != token) {
                if (getReq() == reqId) setReq(null)
                false
            } else { ps.vlcPassword = pw; true }
        }

    /** After a successful ack: forget the request id only if it is still [reqId]; a newer pairing's or operation's id is never touched. */
    internal fun clearOwnRequest(ps: SecurePairStore, token: String, reqId: String, getReq: () -> String?, setReq: (String?) -> Unit) {
        SecurePairSave.underWriteLock { if (getReq() == reqId) setReq(null) }
    }

    /** Stored form of the request id: a short hash of the pairing token, then the id. Only the id goes on the wire. */
    internal fun tag(token: String, id: String): String =
        java.security.MessageDigest.getInstance("SHA-256").digest(token.toByteArray()).take(8).joinToString("") { "%02x".format(it) } + "|" + id

    /** (needsStore, id): reuse the stored id only when it carries this PC's tag; anything else (none, untagged, another pairing) gets a fresh id. */
    internal fun requestSlot(token: String, stored: String?): Pair<Boolean, String> {
        val prefix = tag(token, "").substringBefore('|') + "|"
        return if (stored != null && stored.startsWith(prefix) && stored.length > prefix.length) Pair(false, stored.substring(prefix.length))
        else Pair(true, java.util.UUID.randomUUID().toString())
    }

    /** Stores a freshly minted request slot only while the stored pairing is still the PC with [token], under the write lock. */
    internal fun mintSlot(ps: SecurePairStore, token: String, slot: String, setReq: (String) -> Unit): Boolean =
        SecurePairSave.underWriteLock {
            val cur = try { (SecurePairSave.load(ps) as? PairedCredential.State.Secure)?.credential } catch (e: Exception) { null }
            if (cur != null && cur.token == token) { setReq(slot); true } else false
        }

    @Synchronized
    fun tryHealOnce(store: Store, expectedToken: String): Boolean {
        if (healing) return false
        if (store.host.isBlank() || store.helperToken.isBlank()) return false
        healing = true
        try {
            // Persist the request id before sending: a crash or a lost response replays the
            // same id (the helper re-serves it) instead of burning the one-shot grant.
            // One endpoint for the whole heal (adopt, password write, ack): the PC is identified by its pairing token, which a move keeps
            // and a re-pair replaces. Captured BEFORE the request id so the id belongs to this PC.
            val ep = store.helperEndpoint()
            // The caller's logical operation began against one PC: a pairing that changed since then is not ours to heal. The mint below is
            // guarded under the write lock against this same token, so the check and the first effect cannot be split by a re-pair.
            if (ep.token != expectedToken) return false
            // The persisted id is bound to the PC's token: an id left from another pairing is never replayed to this one.
            val bound = requestSlot(ep.token, store.vlcAdoptRequestId)
            val reqId = bound.second
            if (bound.first) {
                // Mint only while the stored pairing is still the PC we captured, under the pairing write lock: a heal that raced a re-pair
                // never leaves an id for the old PC in the new pairing's slot. (A stale tagged id would be ignored anyway; see requestSlot.)
                if (!mintSlot(store.securePairStore, ep.token, tag(ep.token, reqId)) { store.vlcAdoptRequestId = it }) return false
            }
            val slot = tag(ep.token, reqId)
            val pw = HelperClient.adoptVlcPassword(ep.host, ep.port, ep.token, reqId)
                ?: return false
            // note: adopted password is applied in memory by storeAdopted; it is acknowledged to the PC only after commitPairing() reports true
            if (!storeAdopted(store.securePairStore, ep.token, pw, slot, { store.vlcAdoptRequestId }, { store.vlcAdoptRequestId = it })) return false
            // Never ack (and spend the PC's one-shot grant) for a password the phone could not report as written. The request slot stays, so a
            // retry inside the helper's retry window replays the same id. commitPairing()==true is the store's own report, not a power-loss guarantee.
            if (!store.commitPairing()) return false
            if (HelperClient.ackVlcAdoption(ep.host, ep.port, ep.token, reqId)) {
                clearOwnRequest(store.securePairStore, ep.token, slot, { store.vlcAdoptRequestId }, { store.vlcAdoptRequestId = it })
            }
            return true
        } catch (e: Exception) {
            return false
        } finally {
            healing = false
        }
    }
}
