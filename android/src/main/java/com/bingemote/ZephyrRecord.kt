package com.bingemote

/**
 * The process-wide owner of one stored pairing record: ONE lock and ONE cache shared by every Store instance in the process
 * (the media service builds its own Store per action). Reads after the first load are volatile reads with no disk access.
 * A write holds the lock, marks the record unloaded, runs the caller's writes, then publishes the new whole value, so a reader
 * sees the old value or the new one, and [peek] reports nothing while a write is in flight or after a write that threw.
 */
class ZephyrRecord<T : Any> {
    private val lock = Any()
    @Volatile private var cache: T? = null
    @Volatile private var loaded = false

    /** What a pin check can tell: [Unavailable] = not loaded, a write in flight, or a failed write (says nothing about the pairing);
     *  [Value] = the whole stored value, null meaning nothing is paired. */
    sealed class Peek<out T> { object Unavailable : Peek<Nothing>(); class Value<T>(val v: T?) : Peek<T>() }
    fun peekState(): Peek<T> = if (loaded) Peek.Value(cache) else Peek.Unavailable

    /** The loaded value only; null when unpaired, never loaded, or a write is in flight. Never touches storage. */
    fun peek(): T? = if (loaded) cache else null

    /** The value, loading it once through [load] under the lock. A throwing [load] caches nothing. */
    fun read(load: () -> T?): T? {
        if (loaded) return cache
        return synchronized(lock) {
            if (loaded) cache else { val v = load(); cache = v; loaded = true; v }
        }
    }

    internal fun resetForTests() { synchronized(lock) { loaded = false; cache = null } }

    /** [block] performs the stored writes and returns the new whole value. If it throws, the record stays unloaded and the next read reloads from storage. */
    fun write(block: () -> T?) {
        synchronized(lock) {
            loaded = false
            val v = block()
            cache = v
            loaded = true
        }
    }
}
