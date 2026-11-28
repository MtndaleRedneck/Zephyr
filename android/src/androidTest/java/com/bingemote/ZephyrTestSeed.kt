package com.bingemote

/** Test-only way to put the app's Zephyr pairing into a known state through the real save protocol, and to clear it again. */
object ZephyrTestSeed {
    const val TOKEN = "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1"
    val PIN: String get() = TlsFakePlayer.pinHex

    fun seed(store: Store, host: String, port: Int, token: String = TOKEN, pinHex: String = PIN, name: String = "") {
        val r = store.saveZephyrPairing(SecureHttp.BoundPin(pinHex, token), host, port, name)
        check(r == SecurePairSave.Outcome.SAVED) { "test seed did not save: $r" }
    }

    /** Back to unpaired: record and marker removed (test teardown only; the app has no reset path in this slice). */
    fun clear(store: Store) {
        store.zephyrPairStore.intent = ""
        store.zephyrPairStore.credential = ""
        Store.resetZephyrRecordForTests()
    }

    const val HELPER_TOKEN = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2"

    /** Test-only: a Secure helper pairing through the real save protocol, so [Store.isConfigured] is true. Used by fixtures that drive VLC
     *  through a local stub: the helper port may be dead, but the pairing record must exist because the phone no longer treats an
     *  unpaired plain-token setup as configured. The pin is the fake's key; [vlcPassword] is stored with it as in a real pairing. */
    fun seedHelper(store: Store, host: String, helperPort: Int, vlcPassword: String = "x", token: String = HELPER_TOKEN, pinHex: String = PIN) {
        val r = SecurePairSave.save(store.securePairStore, host, helperPort, "", vlcPassword, SecureHttp.BoundPin(pinHex, token), PairedCredential.newPcId())
        check(r == SecurePairSave.Outcome.SAVED) { "test helper seed did not save: $r" }
    }

    /** Back to no helper pairing (test teardown only). */
    fun clearHelper(store: Store) {
        store.securePairStore.intent = ""
        store.securePairStore.credential = ""
    }
}
