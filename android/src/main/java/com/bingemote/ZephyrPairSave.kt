package com.bingemote

/** Everything one Zephyr pairing persists. Its own keys: nothing here is the helper's. */
internal interface ZephyrPairStore {
    var credential: String      // encoded ZephyrCredential, one value
    var intent: String          // write-ahead marker: non-empty means a save started and did not finish
    var pcName: String
    fun flush(): Boolean
    fun healthy(): Boolean
}

/**
 * Secure-only Zephyr save with the same write-ahead protocol as [SecurePairSave], on its own store, intent marker and in-process latch,
 * so a blocked Zephyr save never touches the helper's state and the reverse. It shares only the one write lock (SecurePairSave.underWriteLock)
 * so the two cannot interleave. Old plain-HTTP data is never read or deleted here.
 */
internal object ZephyrPairSave {
    private val latched: MutableSet<ZephyrPairStore> = java.util.Collections.newSetFromMap(java.util.Collections.synchronizedMap(java.util.WeakHashMap()))
    fun isLatched(s: ZephyrPairStore) = latched.contains(s)

    fun save(s: ZephyrPairStore, host: String, port: Int, pcName: String, proof: SecureHttp.BoundPin, pcId: String): SecurePairSave.Outcome =
        SecurePairSave.underWriteLock {
            val r = saveInner(s, host, port, pcName, proof, pcId)
            if (r == SecurePairSave.Outcome.FAILED_BLOCKED) latched.add(s) else if (r == SecurePairSave.Outcome.SAVED) latched.remove(s)
            r
        }

    private fun saveInner(s: ZephyrPairStore, host: String, port: Int, pcName: String, proof: SecureHttp.BoundPin, pcId: String): SecurePairSave.Outcome {
        val record = ZephyrCredential.of(pcId, proof.pinHex, host, port, proof.token)?.encode() ?: return SecurePairSave.Outcome.FAILED_PRIOR_KEPT
        val priorCred: String; val priorName: String
        try {
            if (!s.healthy()) return SecurePairSave.Outcome.FAILED_PRIOR_KEPT
            priorCred = s.credential; priorName = s.pcName
            if (!s.healthy()) return SecurePairSave.Outcome.FAILED_PRIOR_KEPT
        } catch (e: Exception) { return SecurePairSave.Outcome.FAILED_PRIOR_KEPT }
        val intentOk = runCatching { s.intent = "saving"; s.flush() }.getOrDefault(false)
        if (!intentOk) {
            runCatching { s.intent = ""; s.flush() }
            return SecurePairSave.Outcome.FAILED_PRIOR_KEPT
        }
        val mutated = runCatching {
            s.credential = record
            s.pcName = pcName
            s.flush() && s.credential == record && s.pcName == pcName
        }.getOrDefault(false)
        if (!mutated) {
            val restored = runCatching {
                s.credential = priorCred
                s.pcName = priorName
                s.flush() && s.credential == priorCred && s.pcName == priorName
            }.getOrDefault(false)
            val cleared = restored && runCatching { s.intent = ""; s.flush() && s.intent.isEmpty() }.getOrDefault(false)
            return if (cleared) SecurePairSave.Outcome.FAILED_PRIOR_KEPT else SecurePairSave.Outcome.FAILED_BLOCKED
        }
        val done = runCatching { s.intent = ""; s.flush() && s.intent.isEmpty() }.getOrDefault(false)
        return if (done) SecurePairSave.Outcome.SAVED else SecurePairSave.Outcome.FAILED_BLOCKED
    }

    /**
     * Verified address move of the stored Zephyr record: the same pcId, key and token, only host and port replaced. Runs inside the same write lock as [save].
     * REFUSED, nothing written, when latched, unhealthy, a save is in flight, the stored record is not exactly [expectedRecord] (the full record is the
     * generation: a re-pair or another move invalidates it) or the PC was marked key-changed. Results map exactly like the helper's SecurePairSave.updateAddress.
     * Callers that publish the process-wide cache must wrap this in the record's write (Store.moveZephyrAddress); alone it only touches storage.
     */
    internal fun updateAddress(s: ZephyrPairStore, expectedRecord: String, host: String, port: Int): SecurePairSave.MoveResult =
        SecurePairSave.underWriteLock { updateAddressLocked(s, expectedRecord, host, port) }

    private fun updateAddressLocked(s: ZephyrPairStore, expectedRecord: String, host: String, port: Int): SecurePairSave.MoveResult {
        if (isLatched(s)) return SecurePairSave.MoveResult.REFUSED
        val old: ZephyrCredential; val oldRaw: String
        try {
            if (!s.healthy() || s.intent.isNotEmpty()) return SecurePairSave.MoveResult.REFUSED
            oldRaw = s.credential
            if (oldRaw != expectedRecord) return SecurePairSave.MoveResult.REFUSED
            old = ZephyrCredential.decode(oldRaw) ?: return SecurePairSave.MoveResult.REFUSED
        } catch (e: Exception) { return SecurePairSave.MoveResult.REFUSED }
        if (KeyChangeState.isChanged(old.pcId)) return SecurePairSave.MoveResult.REFUSED
        val newRec = ZephyrCredential.of(old.pcId, old.pinHex, host, port, old.token)?.encode() ?: return SecurePairSave.MoveResult.REFUSED
        if (newRec == oldRaw) return SecurePairSave.MoveResult.UNCHANGED
        val committed = try { s.credential = newRec; true } catch (e: Exception) { false }
        val after = try { if (s.healthy()) s.credential else null } catch (e: Exception) { null }
        if (committed && after == newRec) return SecurePairSave.MoveResult.COMMITTED
        val c = after?.let { ZephyrCredential.decode(it) }
        if (c != null && after == oldRaw && !committed) return SecurePairSave.MoveResult.REFUSED
        if (c != null && after == newRec) return SecurePairSave.MoveResult.SESSION_USABLE
        latched.add(s)
        return SecurePairSave.MoveResult.BLOCKED
    }

    /**
     * [updateAddress] plus the post-write proof (C1): the record read back afterwards must equal the verified record with ONLY host and port replaced
     * (same pcId, key and token). Anything else is never reported as a move. The re-read is this function's own, never a carried value.
     */
    internal fun commitMove(s: ZephyrPairStore, v: SecureHttp.VerifiedAddress): ZephyrMove.Commit {
        val old = ZephyrCredential.decode(v.record) ?: return ZephyrMove.Commit.Refused
        val expectedNew = ZephyrCredential.of(old.pcId, old.pinHex, v.host, v.port, old.token)?.encode() ?: return ZephyrMove.Commit.Refused
        val r = updateAddress(s, v.record, v.host, v.port)
        if (r == SecurePairSave.MoveResult.REFUSED) return ZephyrMove.Commit.Refused
        if (r == SecurePairSave.MoveResult.BLOCKED) return ZephyrMove.Commit.Blocked
        val raw = try { s.credential } catch (e: Exception) { return ZephyrMove.Commit.Blocked }
        val fresh = ZephyrCredential.decode(raw)
        // Only a COMMITTED result is a confirmed commit; UNCHANGED and SESSION_USABLE moved nothing durable and never raise a notice.
        return if (fresh == null || raw != expectedNew) ZephyrMove.Commit.Refused
        else ZephyrMove.Commit.Written(fresh, committed = r == SecurePairSave.MoveResult.COMMITTED)
    }

    /** What a freshly loaded phone should do. [legacyPresent] only reports that an old plain-HTTP pairing exists; its values are never read. */
    fun load(s: ZephyrPairStore, legacyPresent: () -> Boolean): ZephyrCredential.State {
        if (isLatched(s)) return ZephyrCredential.State.Blocked
        val cred = runCatching { s.credential }.getOrNull(); val intent = runCatching { s.intent }.getOrNull()
        val legacy = runCatching { legacyPresent() }.getOrNull()
        val broken = cred == null || intent == null || legacy == null || !runCatching { s.healthy() }.getOrDefault(false)
        return ZephyrCredential.Resolver.resolve(cred ?: "", intent ?: "", legacy ?: false, broken)
    }
}
