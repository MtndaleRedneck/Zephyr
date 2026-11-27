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

    /** What a freshly loaded phone should do. [legacyPresent] only reports that an old plain-HTTP pairing exists; its values are never read. */
    fun load(s: ZephyrPairStore, legacyPresent: () -> Boolean): ZephyrCredential.State {
        if (isLatched(s)) return ZephyrCredential.State.Blocked
        val cred = runCatching { s.credential }.getOrNull(); val intent = runCatching { s.intent }.getOrNull()
        val legacy = runCatching { legacyPresent() }.getOrNull()
        val broken = cred == null || intent == null || legacy == null || !runCatching { s.healthy() }.getOrDefault(false)
        return ZephyrCredential.Resolver.resolve(cred ?: "", intent ?: "", legacy ?: false, broken)
    }
}
