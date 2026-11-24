package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The real Store, over in-memory preferences that can fail like a damaged secret store:
 *  the pairing save, the poison rule and the legacy zephyrToken getter wired together. */
class StorePairingWiringTest {
    private class Prefs(
        initial: Map<String, Any?> = emptyMap(),
        var throwOnRead: Set<String> = emptySet(),
        var commitOk: Boolean = true,
    ) : android.content.SharedPreferences {
        val map = HashMap(initial)
        val writtenKeys = LinkedHashSet<String>()
        override fun getAll(): MutableMap<String, *> = HashMap(map)
        override fun getString(key: String?, defValue: String?): String? {
            if (key in throwOnRead) throw RuntimeException("cannot decrypt $key")
            return map[key] as? String ?: defValue
        }
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
            override fun commit(): Boolean { applyStaged(); return commitOk }
            override fun apply() = applyStaged()
            private fun applyStaged() {
                removes.forEach { map.remove(it); writtenKeys.add(it) }
                puts.forEach { (k, v) -> if (v == null) map.remove(k) else map[k] = v; writtenKeys.add(k) }
            }
        }
    }

    private fun fields(store: Store) = object : PairFields {
        override var host: String get() = store.host; set(v) { store.host = v }
        override var helperPort: Int get() = store.helperPort; set(v) { store.helperPort = v }
        override var helperToken: String get() = store.helperToken; set(v) { store.helperToken = v }
        override var pcName: String get() = store.pcName; set(v) { store.pcName = v }
        override var vlcPassword: String get() = store.vlcPassword; set(v) { store.vlcPassword = v }
        override fun flush(): Boolean = store.commitPairing()
    }

    @Test fun unreadableOldVlcPasswordStopsTheSaveBeforeAnyWrite() {
        val secrets = Prefs(mapOf("helperToken" to "oldtoken", "vlcPassword" to "oldvlc"), throwOnRead = setOf("vlcPassword"))
        val plain = Prefs(mapOf("host" to "oldhost", "helperPort" to 8492, "pcName" to "old"))
        val store = Store(plain, secrets)
        val out = HelperPairSave.save(fields(store), "newhost", 8492, "newtoken", "New", "newvlc")
        assertEquals(HelperPairSave.Outcome.FAILED_PRIOR_KEPT, out)
        assertFalse("no secret was written", secrets.writtenKeys.contains("helperToken"))
        assertEquals("oldhost", plain.map["host"])
        assertEquals("oldtoken", secrets.map["helperToken"])
    }

    @Test fun refusedSecretCommitRollsTheRealStoreBackToThePriorPairing() {
        val secrets = Prefs(mapOf("helperToken" to "oldtoken", "vlcPassword" to "oldvlc"))
        val plain = Prefs(mapOf("host" to "oldhost", "helperPort" to 8492, "pcName" to "old"))
        val store = Store(plain, secrets)
        secrets.commitOk = false
        val out = HelperPairSave.save(fields(store), "newhost", 8493, "newtoken", "New", "newvlc")
        assertEquals(HelperPairSave.Outcome.FAILED_RESTORE_UNCERTAIN, out) // the restore could not be committed either
        assertEquals("oldtoken", secrets.map["helperToken"])
        assertEquals("oldhost", plain.map["host"])
        assertEquals(8492, plain.map["helperPort"])
    }

    @Test fun healthySaveCommitsAndPersists() {
        val secrets = Prefs(); val plain = Prefs()
        val store = Store(plain, secrets)
        assertEquals(HelperPairSave.Outcome.SAVED, HelperPairSave.save(fields(store), "h", 8492, "tok", "PC", "pw"))
        assertEquals("tok", secrets.map["helperToken"]); assertEquals("h", plain.map["host"])
    }

    @Test fun poisonedStoreReportsBrokenAndRefusesCommit() {
        val secrets = Prefs(mapOf("helperToken" to "t"), throwOnRead = setOf("helperToken"))
        val store = Store(Prefs(), secrets)
        assertEquals("", store.helperToken) // failed read
        assertTrue(store.secureStoreBroken)
        assertFalse(store.commitPairing())
    }

    @Test fun legacyPlaintextZephyrTokenGetterDoesNotThrowWhenTheBoxIsPoisoned() {
        val secrets = Prefs(mapOf("helperToken" to "t"), throwOnRead = setOf("helperToken"))
        val plain = Prefs(mapOf("zephyrToken" to "legacy-plain"))
        val store = Store(plain, secrets)
        store.helperToken // poisons the box
        assertEquals("legacy-plain", store.zephyrToken) // no uncaught SecureStoreUnavailable, still readable
        assertEquals("legacy-plain", plain.map["zephyrToken"]) // not migrated, not deleted
        assertFalse(secrets.writtenKeys.contains("zephyrToken"))
    }

    @Test fun legacyPlaintextZephyrTokenMigratesWhenHealthy() {
        val secrets = Prefs(); val plain = Prefs(mapOf("zephyrToken" to "legacy-plain"))
        val store = Store(plain, secrets)
        assertEquals("legacy-plain", store.zephyrToken)
        assertEquals("legacy-plain", secrets.map["zephyrToken"])
        assertFalse(plain.map.containsKey("zephyrToken"))
    }
}
