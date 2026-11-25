package com.bingemote

/** The phone-side fields one helper pairing writes. */
internal interface PairFields {
    var host: String
    var helperPort: Int
    var helperToken: String
    var pcName: String
    var vlcPassword: String
    /** Force the writes to disk and report whether they landed (SharedPreferences.commit). */
    fun flush(): Boolean = true
    /** False when a prior read failed or the secret store is broken: the real Store getters swallow read errors and poison the box instead of throwing. */
    fun healthy(): Boolean = true
}

/**
 * Local save of a helper pairing with rollback. The PC has already accepted the phone by the
 * time this runs, so a failure here means "the PC may list this phone, this phone did not
 * keep it". Fields are written token first; if any write throws, every field written so far
 * is put back and re-read to confirm. Pure JVM so unit tests pin it.
 */
internal object HelperPairSave {
    enum class Outcome { SAVED, FAILED_PRIOR_KEPT, FAILED_RESTORE_UNCERTAIN }

    fun save(f: PairFields, host: String, port: Int, token: String, pcName: String, vlcPassword: String?): Outcome {
        // Read every prior value BEFORE any write. A protected read that fails here (and poisons the
        // secret store) must not happen after the new token is already written, where the rollback
        // would then be refused. Nothing has changed yet, so this is a clean "prior kept".
        val priorToken: String; val priorHost: String; val priorPort: Int; val priorName: String; val priorVlc: String?
        try {
            priorToken = f.helperToken; priorHost = f.host; priorPort = f.helperPort; priorName = f.pcName
            priorVlc = if (vlcPassword != null) f.vlcPassword else null
        } catch (e: Exception) {
            return Outcome.FAILED_PRIOR_KEPT
        }
        // The real getters do not throw: a failed protected read returns "" and poisons the box. Check
        // health after collecting priors; unhealthy means nothing was written and nothing can be, so the
        // old pairing is untouched (not UNCERTAIN: no write happened).
        if (!runCatching { f.healthy() }.getOrDefault(false)) return Outcome.FAILED_PRIOR_KEPT
        val undo = ArrayDeque<() -> Boolean>() // each restores one written field, true when confirmed
        fun <T> step(read: () -> T, write: (T) -> Unit, prior: T, value: T) {
            // Register before writing: a write that throws midway may still have changed state.
            undo.addLast { runCatching { write(prior) }; runCatching { read() == prior }.getOrDefault(false) }
            write(value)
        }
        return try {
            step({ f.helperToken }, { f.helperToken = it }, priorToken, token)
            step({ f.host }, { f.host = it }, priorHost, host)
            step({ f.helperPort }, { f.helperPort = it }, priorPort, port)
            step({ f.pcName }, { f.pcName = it }, priorName, pcName)
            if (vlcPassword != null) step({ f.vlcPassword }, { f.vlcPassword = it }, priorVlc!!, vlcPassword)
            // apply() alone cannot say the write reached disk; a refused commit rolls everything back.
            if (!f.flush()) throw IllegalStateException("pairing not durable")
            Outcome.SAVED
        } catch (e: Exception) {
            var ok = true
            while (undo.isNotEmpty()) if (!undo.removeLast()()) ok = false
            if (ok && !runCatching { f.flush() }.getOrDefault(false)) ok = false // the restored values must be durable too
            if (ok) Outcome.FAILED_PRIOR_KEPT else Outcome.FAILED_RESTORE_UNCERTAIN
        }
    }

    const val PRIOR_KEPT_MESSAGE = "The PC accepted this phone, but this phone couldn't save the pairing (secure storage problem). Your earlier pairing on this phone is unchanged. The PC may now list this phone: remove it in Zephyr VLC Remote, then pair again."
    const val UNCERTAIN_MESSAGE = "The PC accepted this phone, but this phone couldn't save the pairing (secure storage problem), and the earlier pairing may not have been fully restored. Pair again from Settings."
}
