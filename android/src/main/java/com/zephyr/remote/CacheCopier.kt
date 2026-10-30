package com.zephyr.remote

import java.io.File

/** Phone-stream cache copy policy (C12/S9), pure JVM for tests.
 *  A non-seekable provider is copied once, then the copy is served.
 *  Rules: one in-flight copy per URI (caller's lock), free space is re-checked
 *  AS BYTES GROW (a fixed floor alone cannot hold an 8 GB copy), and a partial
 *  copy is always deleted - never renamed, never reused. */
internal object CacheCopier {

    class TooLarge : Exception()
    class NoSpace : Exception()

    /** Streams from [open] into [tmp], renames to [final] only when COMPLETE.
     *  [freeSpace] is probed per chunk; [maxBytes] caps the copy. Throws TooLarge /
     *  NoSpace / Exception on failure, with [tmp] deleted and [final] untouched.
     *  [commit] owns the final rename so the caller can arbitrate commit against
     *  request abandonment atomically (default: plain renameTo). */
    fun copy(
        open: () -> java.io.InputStream?,
        tmp: File, final: File,
        freeSpace: () -> Long, maxBytes: Long, minFree: Long,
        cancelled: () -> Boolean = { false },
        commit: (File, File) -> Boolean = { t, f -> t.renameTo(f) },
    ) {
        try {
            if (freeSpace() < minFree) throw NoSpace()
            val inp = open() ?: throw java.io.IOException("no stream")
            inp.use { i ->
                tmp.outputStream().use { o ->
                    val buf = ByteArray(64 * 1024)
                    var total = 0L
                    var n = i.read(buf)
                    while (n >= 0) {
                        if (cancelled()) throw java.io.InterruptedIOException("copy stopped")
                        total += n
                        if (total > maxBytes) throw TooLarge()
                        // Space is consumed as we write: the floor must hold for the
                        // REST of the copy, not just its first byte.
                        if (freeSpace() < minFree) throw NoSpace()
                        o.write(buf, 0, n)
                        n = i.read(buf)
                    }
                }
            }
            if (cancelled()) throw java.io.InterruptedIOException("copy stopped")
            if (!commit(tmp, final)) throw java.io.IOException("cache commit refused or failed")
        } catch (e: Throwable) {
            tmp.delete()
            throw e
        }
    }
}
