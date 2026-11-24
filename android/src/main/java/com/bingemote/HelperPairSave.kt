package com.bingemote

/** The phone-side fields one helper pairing writes. */
internal interface PairFields {
    var host: String
    var helperPort: Int
    var helperToken: String
    var pcName: String
    var vlcPassword: String
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
        val undo = ArrayDeque<() -> Boolean>() // each restores one written field, true when confirmed
        fun <T> step(read: () -> T, write: (T) -> Unit, value: T) {
            val prior = read()
            // Register before writing: a write that throws midway may still have changed state.
            undo.addLast { runCatching { write(prior) }; runCatching { read() == prior }.getOrDefault(false) }
            write(value)
        }
        return try {
            step({ f.helperToken }, { f.helperToken = it }, token)
            step({ f.host }, { f.host = it }, host)
            step({ f.helperPort }, { f.helperPort = it }, port)
            step({ f.pcName }, { f.pcName = it }, pcName)
            if (vlcPassword != null) step({ f.vlcPassword }, { f.vlcPassword = it }, vlcPassword)
            Outcome.SAVED
        } catch (e: Exception) {
            var ok = true
            while (undo.isNotEmpty()) if (!undo.removeLast()()) ok = false
            if (ok) Outcome.FAILED_PRIOR_KEPT else Outcome.FAILED_RESTORE_UNCERTAIN
        }
    }

    const val PRIOR_KEPT_MESSAGE = "The PC accepted this phone, but this phone couldn't save the pairing (secure storage problem). Your earlier pairing on this phone is unchanged. The PC may now list this phone: remove it in Zephyr VLC Remote, then pair again."
    const val UNCERTAIN_MESSAGE = "The PC accepted this phone, but this phone couldn't save the pairing (secure storage problem), and the earlier pairing may not have been fully restored. Pair again from Settings."
}
