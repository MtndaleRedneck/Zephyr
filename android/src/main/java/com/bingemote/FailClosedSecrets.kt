package com.bingemote

/** Thrown when a secret write is attempted while secure storage is unavailable.
 *  Callers surface a plain error; nothing is fake-persisted. */
class SecureStoreUnavailable : Exception("secure storage unavailable")

/**
 * Fail-closed secret-store policy (teardown C11), pure JVM so unit tests pin it:
 *  - broken store: reads return "" (the app looks UNPAIRED, never falsely paired),
 *    writes THROW SecureStoreUnavailable (a new pair can never silently vanish at
 *    restart), and nothing is ever deleted - the on-disk encrypted file is untouched.
 *  - healthy store: reads/writes pass through; a throwing read degrades to "".
 */
internal class FailClosedSecrets(
    private val reader: (String) -> String?,
    private val writer: (String, String?) -> Unit,
    val broken: Boolean,
) {
    fun read(key: String): String = try { reader(key) ?: "" } catch (e: Exception) { "" }

    fun write(key: String, value: String?) {
        if (broken) throw SecureStoreUnavailable()
        writer(key, value)
    }
}
