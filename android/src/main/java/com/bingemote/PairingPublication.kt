package com.bingemote

/** What the publication needs from a store: load the whole helper/VLC tuple, and say whether a secret read has failed since startup. */
internal interface PairingPublicationSource {
    fun loadTuple(): Store.HelperVlcSnapshot
    fun isBroken(): Boolean
}

/**
 * The process-wide published helper/VLC tuple (host, helper port, token, VLC port, VLC password) exactly as the getters return it for
 * every pairing state, so the Main thread can compare the PC it acted for without touching storage.
 *
 * - Only the OUTERMOST writer section changes it: entry unpublishes, exit (in a finally, so also after a failed or restored write)
 *   loads the stored tuple and publishes the complete value. Inner sections and inner setters never publish.
 * - A reader publishes only inside the same write lock and only when no writer section is open, so it cannot overwrite a newer or an
 *   in-flight writer.
 * - After a load, a broken secret store (monotonic) discards that tuple and reloads once, so a tuple mixed by a mid-load failure is never
 *   published or returned.
 * - A failed secret read anywhere calls [invalidate] at once (no storage work): the published tuple is dropped and any load that started
 *   before it is not published (epoch check inside [pubLock], which no storage path takes), so a stale good peek cannot outlive a poison.
 * - The poison drop is not atomic with the failed read: a thread descheduled between the flag and [invalidate] leaves the old good tuple
 *   visible to peek for that gap. No atomic-safety claim.
 * - Process-global by design: it assumes every Store instance in the process (the UI and the media service each build one) reads the same
 *   backing storage. Distinct backing stores in one process (tests) must reset it; a poison in any of them drops the one published tuple.
 * - [peek] is lock-free and never reads storage. Null means unloaded, a writer section in flight, or just poisoned: not current.
 * Depth is mutated only with [SecurePairSave]'s write lock held (callers: SecurePairSave.writerSection / readAndPublish).
 */
internal object PairingPublication {
    @Volatile private var published: Store.HelperVlcSnapshot? = null
    private var depth = 0
    /** Guards published and epoch only. Held for a compare-and-set, never across a load, so nothing waits on storage under it. */
    private val pubLock = Any()
    private var epoch = 0

    fun peek(): Store.HelperVlcSnapshot? = published

    /** Called by the secret box on its first failed read, on any thread. No storage work, no write lock. */
    fun invalidate() { synchronized(pubLock) { epoch++; published = null } }

    private fun setPublished(t: Store.HelperVlcSnapshot?) { synchronized(pubLock) { published = t } }

    private fun loadNormalized(src: PairingPublicationSource): Store.HelperVlcSnapshot {
        val first = src.loadTuple()
        return if (src.isBroken()) src.loadTuple() else first
    }

    /** Loads a normalized tuple and publishes it only if no poison arrived while loading; a poison during the load triggers one more load
     *  (the box is broken by then, so it is the blank tuple). Returns the tuple loaded last. */
    private fun loadAndPublish(src: PairingPublicationSource, publish: Boolean): Store.HelperVlcSnapshot {
        var t: Store.HelperVlcSnapshot? = null
        repeat(2) {
            val e = synchronized(pubLock) { epoch }
            val n = loadNormalized(src); t = n
            if (!publish) return n
            synchronized(pubLock) { if (epoch == e) { published = n; return n } }
        }
        return t!!
    }

    /** Caller holds the write lock. A null source (a test double with no Store behind it) publishes nothing. */
    fun <T> section(src: PairingPublicationSource?, block: () -> T): T {
        if (src == null) return block()
        depth++
        if (depth == 1) setPublished(null)
        try {
            return block()
        } finally {
            depth--
            if (depth == 0) try { loadAndPublish(src, true) } catch (e: Exception) { setPublished(null) }
        }
    }

    /** Caller holds the write lock. Returns the normalized tuple; publishes it only outside a writer section. */
    fun readAndPublish(src: PairingPublicationSource): Store.HelperVlcSnapshot = loadAndPublish(src, depth == 0)

    /** Test only: forget everything, under the same owner lock the writers use. */
    internal fun resetForTests() { SecurePairSave.underWriteLock { synchronized(pubLock) { published = null; depth = 0; epoch = 0 } } }
}
