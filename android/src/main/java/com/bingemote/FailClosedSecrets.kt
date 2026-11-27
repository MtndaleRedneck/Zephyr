package com.bingemote

/** Thrown when a secret write is attempted while secure storage is unavailable.
 *  Callers surface a plain error; nothing is fake-persisted. */
class SecureStoreUnavailable : Exception("secure storage unavailable")

/**
 * Fail-closed secret-store policy (teardown C11), pure JVM so unit tests pin it:
 *  - broken store: reads return "" (the app looks UNPAIRED, never falsely paired),
 *    writes THROW SecureStoreUnavailable (a new pair can never silently vanish at
 *    restart), and nothing is ever deleted - the on-disk encrypted file is untouched.
 *  - healthy store: reads/writes pass through; a throwing read returns "" AND poisons the store for
 *    the rest of this process, so a later write can never treat the unreadable old secret as
 *    empty and overwrite it (writes then throw like a broken store; nothing is deleted).
 */
internal class FailClosedSecrets(
    private val reader: (String) -> String?,
    private val writer: (String, String?) -> Unit,
    broken: Boolean,
    /** Called once, after the failure flag is set, on the thread that saw the first failed read. Must do no storage work and take no storage-path lock.
     *  Not atomic with the flag: between the flag and this call another thread can still see the old state. The flag-then-callback `first` check
     *  is also not atomic, so a concurrent first failure may call it more than once (harmless for an idempotent callback). */
    private val onPoison: (() -> Unit)? = null,
) {
    @Volatile private var poisoned = false
    private val startedBroken = broken
    /** True when the store failed to open or a read failed earlier in this process. */
    val isBroken: Boolean get() = startedBroken || poisoned

    fun read(key: String): String = if (isBroken) "" else try { reader(key) ?: "" } catch (e: Exception) { val first = !poisoned; poisoned = true; if (first) onPoison?.invoke(); "" }

    fun write(key: String, value: String?) {
        if (isBroken) throw SecureStoreUnavailable()
        writer(key, value)
    }
}
