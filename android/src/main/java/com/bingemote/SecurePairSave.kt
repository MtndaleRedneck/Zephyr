package com.bingemote

/** Everything one secure pairing persists. All required, no defaults, so nothing can be silently dropped. */
internal interface SecurePairStore {
    var credential: String      // encoded PairedCredential (identity + pin + address + token), one value
    var intent: String          // write-ahead marker: non-empty means a save or reset started and did not finish
    var vlcPassword: String
    var pcName: String
    var legacyToken: String
    fun flush(): Boolean
    fun healthy(): Boolean
    /** The published-tuple source for the Store behind this store; null for a double with none. */
    val publication: PairingPublicationSource? get() = null
}

/**
 * Secure-only save and reset with a durable write-ahead marker. The marker is persisted (and flushed)
 * BEFORE anything else changes; if it cannot be persisted nothing is touched. It is cleared only after
 * the final flush succeeds. A crash or a failed cleanup therefore always leaves it set, and a set marker
 * resolves to Blocked on the next load (see [PairedCredential.Resolver]). The block does not depend on
 * any write made after a failure.
 */
internal object SecurePairSave {
    /** In-process latch: once a save or reset could not be proven complete, this store stays Blocked in this process
     *  no matter what the (possibly stale) cache reads, until a later save or reset fully succeeds. */
    private val latched: MutableSet<SecurePairStore> = java.util.Collections.newSetFromMap(java.util.Collections.synchronizedMap(java.util.WeakHashMap()))
    fun isLatched(s: SecurePairStore) = latched.contains(s)

    /** save, forget and updateAddress each run a write-ahead intent protocol on shared storage; one lock keeps two of them
     *  (a pairing screen, a reset and a background address update) from interleaving their marker and record writes. */
    private val writeLock = Any()

    /** Runs [block] under the same lock save, forget and updateAddress hold. For a short local guard-then-write only: no network, no suspension. */
    internal fun <T> underWriteLock(block: () -> T): T = synchronized(writeLock) { block() }

    /** Like [underWriteLock] but for any code that WRITES a field of the helper/VLC tuple: the outermost section unpublishes on entry and publishes
     *  the complete stored tuple on exit (see [PairingPublication]). Reentrant: inner sections never publish. */
    internal fun <T> writerSection(s: SecurePairStore, block: () -> T): T = synchronized(writeLock) { PairingPublication.section(s.publication, block) }

    /** One locked read of the tuple; publishes it only when no writer section is open. */
    internal fun readAndPublish(src: PairingPublicationSource): Store.HelperVlcSnapshot = synchronized(writeLock) { PairingPublication.readAndPublish(src) }

    enum class Outcome {
        SAVED, FAILED_PRIOR_KEPT, FAILED_BLOCKED;
        /** Blocked is never an ordinary retry: the phone must be paired again from the PC's Pair screen. */
        val userMessage: String get() = when (this) {
            SAVED -> "Paired."
            FAILED_PRIOR_KEPT -> "Pairing did not save. Your earlier pairing is unchanged. Try Pair on the PC again."
            FAILED_BLOCKED -> "Pairing could not be saved safely and this phone is no longer paired. Open Pair on the PC, then pair this phone again. Retrying alone will not fix it."
        }
    }

    fun save(s: SecurePairStore, host: String, port: Int, pcName: String, vlcPassword: String?, proof: SecureHttp.BoundPin, pcId: String): Outcome {
        return writerSection(s) {
            val r = saveInner(s, host, port, pcName, vlcPassword, proof, pcId)
            if (r == Outcome.FAILED_BLOCKED) latched.add(s) else if (r == Outcome.SAVED) latched.remove(s)
            r
        }
    }

    private fun saveInner(s: SecurePairStore, host: String, port: Int, pcName: String, vlcPassword: String?, proof: SecureHttp.BoundPin, pcId: String): Outcome {
        val record = PairedCredential.of(pcId, proof.pinHex, host, port, proof.token)?.encode() ?: return Outcome.FAILED_PRIOR_KEPT
        val priorCred: String; val priorVlc: String; val priorName: String
        try {
            if (!s.healthy()) return Outcome.FAILED_PRIOR_KEPT
            priorCred = s.credential; priorVlc = s.vlcPassword; priorName = s.pcName
            if (!s.healthy()) return Outcome.FAILED_PRIOR_KEPT
        } catch (e: Exception) { return Outcome.FAILED_PRIOR_KEPT }
        // 1. durable intent first; abort if it cannot be persisted
        val intentOk = runCatching { s.intent = "saving"; s.flush() }.getOrDefault(false)
        if (!intentOk) {
            // Nothing else was touched. If the marker did land it only blocks (safe); try to clear it.
            runCatching { s.intent = ""; s.flush() }
            return Outcome.FAILED_PRIOR_KEPT
        }
        // 2. mutate
        val mutated = runCatching {
            s.credential = record
            if (vlcPassword != null) s.vlcPassword = vlcPassword
            s.pcName = pcName
            // flush, then read back what is persisted: it must equal what was just written
            s.flush() && s.credential == record && s.pcName == pcName && (vlcPassword == null || s.vlcPassword == vlcPassword)
        }.getOrDefault(false)
        if (!mutated) {
            val restored = runCatching {
                s.credential = priorCred
                if (vlcPassword != null) s.vlcPassword = priorVlc
                s.pcName = priorName
                s.flush() && s.credential == priorCred && (vlcPassword == null || s.vlcPassword == priorVlc)
            }.getOrDefault(false)
            val cleared = restored && runCatching { s.intent = ""; s.flush() && s.intent.isEmpty() }.getOrDefault(false)
            return if (cleared) Outcome.FAILED_PRIOR_KEPT else Outcome.FAILED_BLOCKED   // marker still set on disk: blocked after restart
        }
        // 3. clear intent only after the final flush
        val done = runCatching { s.intent = ""; s.flush() && s.intent.isEmpty() }.getOrDefault(false)
        return if (done) Outcome.SAVED else Outcome.FAILED_BLOCKED
    }

    /** Result of an address move. Only [COMMITTED] means the store reported the commit as successful. */
    enum class MoveResult {
        /** One commit reported success and the record read back as written. A commit report is not a power-loss guarantee. */
        COMMITTED,
        /** The record already had this address; nothing was written. */
        UNCHANGED,
        /** The commit was refused or threw, but a re-read after the failure shows a healthy, strictly decodable record with the same
         *  PC id, pin and token and the new address. Usable in this process; NOT proven saved, and not latched. */
        SESSION_USABLE,
        /** Nothing was written: stale snapshot, key-changed PC, latched or marker-busy store, unhealthy or unreadable before the write. */
        REFUSED,
        /** A write was attempted and the state afterwards could not be verified (unreadable, undecodable, different identity). Latched. */
        BLOCKED;
        val usable: Boolean get() = this == COMMITTED || this == UNCHANGED || this == SESSION_USABLE
    }

    /**
     * Moves the stored PC to an address that already proved the stored key (see SecureHttp.verify). It changes only host and
     * port: identity, pin and token are copied from the record the probe used. [VerifiedAddress] is a code convention, not a
     * security boundary; the record check here and the pin on every later connection carry the safety. If the stored record is no
     * longer the probed snapshot (pair, forget or another change ran meanwhile) nothing is written. It never clears a latched or
     * key-changed state.
     *
     * A move is ONE assignment of ONE record: there is no write-ahead marker, no flush and no restore, because pin and token are
     * copied unchanged and the record is a single string, so no mixed record can exist. Save and forget keep their marker.
     * What the commit reports is the store's report, scoped to the Android SharedPreferences implementation: it is not a claim that the
     * new address survives power loss, and after a failed commit the reopened state is unproven (old or new address).
     */
    fun updateAddress(s: SecurePairStore, v: SecureHttp.VerifiedAddress): MoveResult = updateAddress(s, v.record, v.host, v.port)

    internal fun updateAddress(s: SecurePairStore, expectedRecord: String, host: String, port: Int): MoveResult =
        writerSection(s) { updateAddressLocked(s, expectedRecord, host, port) }

    private fun updateAddressLocked(s: SecurePairStore, expectedRecord: String, host: String, port: Int): MoveResult {
        if (isLatched(s)) return MoveResult.REFUSED
        val old: PairedCredential; val oldRaw: String
        try {
            if (!s.healthy() || s.intent.isNotEmpty()) return MoveResult.REFUSED
            oldRaw = s.credential
            if (oldRaw != expectedRecord) return MoveResult.REFUSED
            old = PairedCredential.decode(oldRaw) ?: return MoveResult.REFUSED
        } catch (e: Exception) { return MoveResult.REFUSED }
        if (KeyChangeState.isChanged(old.pcId)) return MoveResult.REFUSED
        val newRec = PairedCredential.of(old.pcId, old.pinHex, host, port, old.token)?.encode() ?: return MoveResult.REFUSED
        if (newRec == oldRaw) return MoveResult.UNCHANGED
        val committed = try { s.credential = newRec; true } catch (e: Exception) { false }
        val after = try { if (s.healthy()) s.credential else null } catch (e: Exception) { null }
        if (committed && after == newRec) return MoveResult.COMMITTED
        // The commit threw or the read-back differs. Judge by a fresh read taken AFTER the failure.
        val c = after?.let { PairedCredential.decode(it) }
        if (c != null && after == oldRaw && !committed) return MoveResult.REFUSED          // the write never reached the store's memory
        if (c != null && after == newRec) return MoveResult.SESSION_USABLE                  // new address in memory, commit not confirmed
        latched.add(s)                                                                       // unreadable, undecodable, other identity or other address
        return MoveResult.BLOCKED
    }

    /** Persisted reset: the only way out of a secure pairing. Same write-ahead rule. True only when fully reset. */
    fun forget(s: SecurePairStore): Boolean {
        return writerSection(s) {
            val ok = forgetInner(s)
            if (!ok) latched.add(s) else latched.remove(s)
            ok
        }
    }

    private fun forgetInner(s: SecurePairStore): Boolean {
        if (!runCatching { s.intent = "forgetting"; s.flush() }.getOrDefault(false)) return false
        val ok = runCatching {
            s.credential = ""; s.vlcPassword = ""; s.legacyToken = ""
            s.flush() && s.credential.isEmpty()
        }.getOrDefault(false)
        if (!ok) return false
        return runCatching { s.intent = ""; s.flush() && s.intent.isEmpty() }.getOrDefault(false)
    }

    /** What a freshly loaded phone should do, from the persisted content. A broken or unreadable store is Blocked, never absent. */
    fun load(s: SecurePairStore): PairedCredential.State {
        if (isLatched(s)) return PairedCredential.State.Blocked
        val cred = runCatching { s.credential }.getOrNull(); val intent = runCatching { s.intent }.getOrNull(); val legacy = runCatching { s.legacyToken }.getOrNull()
        val broken = cred == null || intent == null || legacy == null || !runCatching { s.healthy() }.getOrDefault(false)
        return PairedCredential.Resolver.resolve(cred ?: "", intent ?: "", legacy ?: "", broken)
    }
}
