package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Test

class HelperPairSaveTest {
    /** In-memory fields; [failOn] makes the nth write of that field throw, as a broken secure store would. */
    private class Fields(val failOn: Set<String> = emptySet(), val failRestore: Set<String> = emptySet()) : PairFields {
        val writes = mutableListOf<String>()
        private var h = "oldhost"; private var p = 8492; private var t = "oldtoken"; private var n = "oldname"; private var v = "oldvlc"
        private fun w(k: String, restoring: Boolean) {
            writes += k
            if (k in failOn && !restoring) throw SecureStoreUnavailable()
            if (k in failRestore && restoring) throw SecureStoreUnavailable()
        }
        private var restoring = false
        fun enterRestore() { restoring = true }
        override var host: String get() = h; set(x) { w("host", restoring); h = x }
        override var helperPort: Int get() = p; set(x) { w("port", restoring); p = x }
        override var helperToken: String get() = t; set(x) { w("token", restoring); t = x }
        override var pcName: String get() = n; set(x) { w("name", restoring); n = x }
        override var vlcPassword: String get() = v; set(x) { w("vlc", restoring); v = x }
    }

    private fun snapshot(f: Fields) = listOf(f.host, f.helperPort.toString(), f.helperToken, f.pcName, f.vlcPassword)
    private val prior = listOf("oldhost", "8492", "oldtoken", "oldname", "oldvlc")

    @Test fun successWritesEverything() {
        val f = Fields()
        assertEquals(HelperPairSave.Outcome.SAVED, HelperPairSave.save(f, "h", 1, "tok", "pc", "pw"))
        assertEquals(listOf("h", "1", "tok", "pc", "pw"), snapshot(f))
    }

    @Test fun tokenWriteFailureLeavesPriorPairingUntouched() {
        val f = Fields(failOn = setOf("token"))
        assertEquals(HelperPairSave.Outcome.FAILED_PRIOR_KEPT, HelperPairSave.save(f, "h", 1, "tok", "pc", "pw"))
        assertEquals(prior, snapshot(f))
        assertEquals("host/port/name must never be touched before the token lands", listOf("token", "token"), f.writes)
    }

    @Test fun lateVlcPasswordFailureRollsBackEarlierWrites() {
        val f = Fields(failOn = setOf("vlc"))
        assertEquals(HelperPairSave.Outcome.FAILED_PRIOR_KEPT, HelperPairSave.save(f, "h", 1, "tok", "pc", "pw"))
        assertEquals(prior, snapshot(f))
    }

    @Test fun nameFailureRollsBackTokenHostPort() {
        val f = Fields(failOn = setOf("name"))
        assertEquals(HelperPairSave.Outcome.FAILED_PRIOR_KEPT, HelperPairSave.save(f, "h", 1, "tok", "pc", null))
        assertEquals(prior, snapshot(f))
    }

    @Test fun failedRestoreIsReportedNotHidden() {
        var token = "oldtoken"
        var tokenWrites = 0
        val f = object : PairFields {
            override var host = "oldhost"; override var helperPort = 8492; override var vlcPassword = "oldvlc"
            override var pcName: String get() = "oldname"; set(_) { throw SecureStoreUnavailable() }
            override var helperToken: String
                get() = token
                set(x) { tokenWrites++; if (tokenWrites == 1) token = x else throw SecureStoreUnavailable() }
        }
        assertEquals(HelperPairSave.Outcome.FAILED_RESTORE_UNCERTAIN, HelperPairSave.save(f, "h", 1, "tok", "pc", null))
        assertEquals("tok", token) // the token restore did not land, and the caller is told
    }
}
