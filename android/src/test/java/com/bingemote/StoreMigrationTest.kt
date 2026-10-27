package com.bingemote

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Round-4: the 1.1 -> 1.2 helper retarget (saved 8488 -> 8492, token kept,
 *  one-time notice) runs against a REAL old config, and the migrated endpoint
 *  is exercised over the actual phone protocol with the saved token. */
class StoreMigrationTest {

    /** Minimal in-memory SharedPreferences that records every written key. */
    private class MapPrefs(initial: Map<String, Any?> = emptyMap()) : android.content.SharedPreferences {
        val map = HashMap(initial)
        val writtenKeys = LinkedHashSet<String>()
        override fun getAll(): MutableMap<String, *> = HashMap(map)
        override fun getString(key: String?, defValue: String?): String? = map[key] as? String ?: defValue
        override fun getStringSet(key: String?, defValues: MutableSet<String>?): MutableSet<String>? = defValues
        override fun getInt(key: String?, defValue: Int): Int = map[key] as? Int ?: defValue
        override fun getLong(key: String?, defValue: Long): Long = map[key] as? Long ?: defValue
        override fun getFloat(key: String?, defValue: Float): Float = map[key] as? Float ?: defValue
        override fun getBoolean(key: String?, defValue: Boolean): Boolean = map[key] as? Boolean ?: defValue
        override fun contains(key: String?): Boolean = map.containsKey(key)
        override fun edit(): android.content.SharedPreferences.Editor = Ed()
        override fun registerOnSharedPreferenceChangeListener(l: android.content.SharedPreferences.OnSharedPreferenceChangeListener?) {}
        override fun unregisterOnSharedPreferenceChangeListener(l: android.content.SharedPreferences.OnSharedPreferenceChangeListener?) {}
        inner class Ed : android.content.SharedPreferences.Editor {
            private val puts = HashMap<String, Any?>()
            private val removes = HashSet<String>()
            private fun stage(key: String?, v: Any?) = apply { key?.let { puts[it] = v; removes.remove(it) } }
            override fun putString(key: String?, v: String?) = stage(key, v)
            override fun putStringSet(key: String?, v: MutableSet<String>?) = stage(key, v)
            override fun putInt(key: String?, v: Int) = stage(key, v)
            override fun putLong(key: String?, v: Long) = stage(key, v)
            override fun putFloat(key: String?, v: Float) = stage(key, v)
            override fun putBoolean(key: String?, v: Boolean) = stage(key, v)
            override fun remove(key: String?) = apply { key?.let { removes.add(it); puts.remove(it) } }
            override fun clear() = apply { puts.clear(); removes.addAll(map.keys) }
            override fun commit(): Boolean { applyStaged(); return true }
            override fun apply() = applyStaged()
            private fun applyStaged() {
                removes.forEach { map.remove(it); writtenKeys.add(it) }
                puts.forEach { (k, v) -> if (v == null) map.remove(k) else map[k] = v; writtenKeys.add(k) }
            }
        }
    }

    /** Exactly what a real 1.1 install saved when it paired with the BingeMote
     *  helper (d74eb60 SetupScreen.kt:123): the legacy port, the host, and the
     *  pairing token in the secrets store. */
    private fun legacy11Config(): Pair<MapPrefs, MapPrefs> {
        val plain = MapPrefs(mapOf(
            "helperPort" to 8488,
            "host" to "192.168.1.50",
            "pcName" to "Office PC",
            "vlcPort" to 8080,
        ))
        val secrets = MapPrefs(mapOf("helperToken" to "a1".repeat(32)))
        return plain to secrets
    }

    @Test fun `legacy 8488 retargets to 8492 with the token kept and the notice flagged`() {
        val (plain, secrets) = legacy11Config()
        val store = Store(plain, secrets)
        assertTrue(store.migrateLegacyHelperPort())
        assertEquals(8492, store.helperPort)
        assertTrue(store.vlcRemoteNoticePending)
        // The saved pairing token is never touched: the PC helper imports it.
        assertEquals("a1".repeat(32), secrets.map["helperToken"])
        // Nothing but the port and the notice flag is rewritten.
        assertEquals(setOf("helperPort", "vlcRemoteNoticePending"), plain.writtenKeys - "seekOnTop" - "autoReconnect")
        // Host and name survive.
        assertEquals("192.168.1.50", store.host)
        assertEquals("Office PC", store.pcName)
    }

    @Test fun `migration is once-only across launches`() {
        val (plain, secrets) = legacy11Config()
        assertTrue(Store(plain, secrets).migrateLegacyHelperPort())
        // Next launch over the same prefs: no re-migration, no new notice write.
        plain.writtenKeys.clear()
        val again = Store(plain, secrets)
        assertFalse(again.migrateLegacyHelperPort())
        assertEquals(8492, again.helperPort)
        assertFalse(plain.writtenKeys.contains("helperPort"))
    }

    @Test fun `fresh install never migrates and never flags the notice`() {
        val plain = MapPrefs() // a fresh 1.2 install: helperPort defaults to 8492
        val store = Store(plain, MapPrefs())
        assertFalse(store.migrateLegacyHelperPort())
        assertFalse(store.vlcRemoteNoticePending)
        assertFalse(plain.writtenKeys.contains("vlcRemoteNoticePending"))
    }

    @Test fun `the phone protocol reaches the migrated endpoint with the saved token`() {
        // The retargeted phone talks the Zephyr VLC Remote HTTP protocol at the
        // migrated port with its OLD token in the Zephyr header (round-4 A3).
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(200).setBody("{}"))
        server.start()
        try {
            val ok = HelperClient.ping("127.0.0.1", server.port, "a1".repeat(32))
            assertTrue(ok)
            val req = server.takeRequest()
            assertEquals("/ping", req.path)
            assertEquals("a1".repeat(32), req.getHeader("X-Zephyr-VLC-Remote-Token"))
        } finally {
            server.shutdown()
        }
    }

    @Test fun `the migrated helper rejects a phone with no token`() {
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(401).setBody("{\"error\":\"unauthorized\"}"))
        server.start()
        try {
            assertFalse(HelperClient.ping("127.0.0.1", server.port, "0".repeat(64)))
        } finally {
            server.shutdown()
        }
    }
}
