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
}
