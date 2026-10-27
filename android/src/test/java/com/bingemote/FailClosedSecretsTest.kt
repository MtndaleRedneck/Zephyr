package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** Teardown C11 policy: broken secure storage fails CLOSED - reads look unpaired,
 *  writes refuse loudly, nothing is ever fake-persisted or deleted. */
class FailClosedSecretsTest {

    @Test
    fun healthyRoundTrips() {
        val map = HashMap<String, String?>()
        val box = FailClosedSecrets(
            reader = { k -> map[k] },
            writer = { k, v -> map[k] = v },
            broken = false,
        )
        box.write("token", "abc")
        assertEquals("abc", box.read("token"))
        box.write("token", null)
        assertEquals(null, map["token"])
    }

    @Test
    fun brokenReadsEmptyNeverFalselyPaired() {
        val box = FailClosedSecrets(reader = { k -> "should-not-be-read" }, writer = { _, _ -> }, broken = true)
        assertEquals("", box.read("token"))
    }

    @Test
    fun brokenWriteThrowsAndPersistsNothing() {
        var writes = 0
        val box = FailClosedSecrets(reader = { k -> null }, writer = { _, _ -> writes++ }, broken = true)
        try {
            box.write("token", "abc")
            fail("broken store must refuse writes")
        } catch (e: SecureStoreUnavailable) { }
        assertEquals(0, writes) // the on-disk store was never touched
    }

    @Test
    fun throwingReadDegradesToEmpty() {
        val box = FailClosedSecrets(
            reader = { k -> throw RuntimeException("corrupt") },
            writer = { _, _ -> },
            broken = false,
        )
        assertEquals("", box.read("token")) // fail closed: unreadable = unpaired, not a crash
    }

    @Test
    fun secureStoreUnavailableIsAnException() {
        assertTrue(SecureStoreUnavailable() is Exception)
    }
}
