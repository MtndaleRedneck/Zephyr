package com.bingemote

/**
 * Publishes the stored pairing to the live-state consumers, from a fresh read at the moment of publication.
 *
 * Thread discipline (the whole correctness argument): call this from the Main thread inside ONE non-suspending block, with no suspension
 * point between the read and [publish]. Every store-changing path (save, address move) publishes from Main after its own write
 * completes. Main is single-threaded, so while this block runs a concurrent re-pair can finish its store write on another thread but its
 * publication can only run after this block, and it then replaces whatever was published here. The live state therefore ends up
 * matching the last store writer. This is a thread-confinement argument, not a lock; it is not proven at runtime here.
 */
internal object LivePublication {
    /**
     * Publishes the CURRENT stored record when it is Secure and, if [expectedRecord] is given, still equal to it. Returns whether it published.
     * A moved or re-paired record that no longer equals the expected one publishes nothing: the newer owner publishes its own.
     */
    fun publishIfCurrent(store: SecurePairStore, expectedRecord: String?, publish: (host: String, port: Int, token: String) -> Unit): Boolean {
        val cur = (SecurePairSave.load(store) as? PairedCredential.State.Secure)?.credential ?: return false
        if (expectedRecord != null && cur.encode() != expectedRecord) return false
        publish(cur.host, cur.port, cur.token)
        return true
    }
}
