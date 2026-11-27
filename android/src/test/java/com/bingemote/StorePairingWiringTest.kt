package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The real Store, over in-memory preferences that can fail like a damaged secret store:
 *  the pairing save, the poison rule and the legacy zephyrToken getter wired together. */
class StorePairingWiringTest {
    @org.junit.Before fun startUnloaded() { Store.resetZephyrRecordForTests(); PairingPublication.resetForTests() }
    @org.junit.After fun endUnloaded() { Store.resetZephyrRecordForTests(); PairingPublication.resetForTests() }

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
        override fun healthy(): Boolean = !store.secureStoreBroken
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

    private val ztok = "a1".repeat(32); private val zpin = "ab".repeat(32)
    private fun proof() = SecureHttp.BoundPin(zpin, ztok)

    @Test fun aTupleCapturedBeforeARePairIsRefusedByTheRealStoreEvenWithTheSameHostPortAndToken() {
        val fx = TlsFixture()
        val saved = com.zephyr.remote.ZephyrClient.pinSource
        try {
            Store.installZephyrPinSource()
            com.zephyr.remote.ZephyrClient.resetClientsForTests()
            val store = Store(Prefs(), Prefs())
            assertEquals(SecurePairSave.Outcome.SAVED, store.saveZephyrPairing(SecureHttp.BoundPin(fx.pinHex, ztok), "127.0.0.1", fx.port, "pc"))
            val t1 = store.zephyrTarget()!!                                        // captured by a caller
            fx.server.enqueue(okhttp3.mockwebserver.MockResponse().setBody("{}"))
            com.zephyr.remote.ZephyrClient.pause(t1)                               // control: it sends while the record is unchanged
            assertEquals(1, fx.server.requestCount)
            // Re-paired between capture and execution: same host, port and token, another key (and the new pairing's own local id).
            val otherPin = PairedCredential.toHex(PinPolicy.hashSpki(byteArrayOf(9, 9, 9)))
            assertEquals(SecurePairSave.Outcome.SAVED, store.saveZephyrPairing(SecureHttp.BoundPin(otherPin, ztok), "127.0.0.1", fx.port, "pc"))
            val t2 = store.zephyrTarget()!!
            assertTrue(t1.host == t2.host && t1.port == t2.port && t1.token == t2.token && t1.pinHex != t2.pinHex)
            var refused = false
            try { com.zephyr.remote.ZephyrClient.pause(t1) } catch (e: com.zephyr.remote.ZephyrClient.PairingChanged) { refused = true }
            assertTrue(refused); assertEquals("nothing was sent for the stale tuple", 1, fx.server.requestCount)
        } finally { com.zephyr.remote.ZephyrClient.pinSource = saved; com.zephyr.remote.ZephyrClient.resetClientsForTests(); fx.close() }
    }

    @Test fun anOldPlainZephyrPairingIsNeedsRepairNeverReadNeverMigratedNeverDeleted() {
        val secrets = Prefs(); val plain = Prefs(mapOf("zephyrToken" to "legacy-plain", "zephyrHost" to "pc.local", "zephyrPort" to 47823))
        val store = Store(plain, secrets)
        assertNull(store.zephyrTarget())
        assertTrue(store.zephyrPairing() === ZephyrCredential.State.NeedsRepair)
        assertEquals("", store.zephyrHost); assertNull(store.zephyrEndpoint)
        assertEquals("legacy-plain", plain.map["zephyrToken"])        // not deleted
        assertTrue(secrets.writtenKeys.isEmpty())                       // not migrated into the secret box
    }

    @Test fun anOldPlainPairingOnABrokenStoreIsBlockedAndUntouched() {
        val secrets = Prefs(mapOf("helperToken" to "t"), throwOnRead = setOf("helperToken"))
        val plain = Prefs(mapOf("zephyrToken" to "legacy-plain", "zephyrHost" to "pc.local"))
        val store = Store(plain, secrets)
        store.helperToken // poisons the box
        assertNull(store.zephyrTarget())
        assertTrue(store.zephyrPairing() === ZephyrCredential.State.Blocked)
        assertEquals("legacy-plain", plain.map["zephyrToken"])
    }

    @Test fun aSecureSaveWritesOnlyZephyrKeysAndRemovesTheOldTokenOnlyAfterTheSaveIsConfirmed() {
        val secrets = Prefs(); val plain = Prefs(mapOf("zephyrToken" to "old-plain", "zephyrHost" to "old.local"))
        val store = Store(plain, secrets)
        assertEquals(SecurePairSave.Outcome.SAVED, store.saveZephyrPairing(proof(), "new.local", 47823, "pc"))
        assertFalse(plain.map.containsKey("zephyrToken"))
        assertTrue((secrets.map["zephyrCredential"] as String).startsWith("z1|"))
        assertEquals(null, secrets.map["zephyrIntent"])                 // marker cleared
        assertFalse(secrets.writtenKeys.any { it.startsWith("helper") }) // helper keys never written
        val t = store.zephyrTarget()!!
        assertEquals(listOf("new.local", 47823, ztok, zpin), listOf(t.host, t.port, t.token, t.pinHex))
        assertTrue(Regex("[0-9a-f]{32}").matches(t.pcId))
        assertEquals("pc", store.zephyrPcName)
        Store.resetZephyrRecordForTests()
        val again = Store(plain, secrets).zephyrTarget()!!                // a reload from storage gives the same whole tuple
        assertTrue(again.same(t))
    }

    @Test fun aRefusedCommitKeepsTheOldPairingAndNeverDeletesIt() {
        val secrets = Prefs(); secrets.commitOk = false
        val plain = Prefs(mapOf("zephyrToken" to "old-plain", "zephyrHost" to "old.local"))
        val store = Store(plain, secrets)
        val r = store.saveZephyrPairing(proof(), "new.local", 47823, "pc")
        assertTrue(r != SecurePairSave.Outcome.SAVED)
        assertEquals("old-plain", plain.map["zephyrToken"])
        assertNull(store.zephyrTarget())                                // never falls back to the old token
    }

    @Test fun aStaleOldTokenNeverOverridesTheSecureRecord() {
        val secrets = Prefs(); val plain = Prefs()
        val store = Store(plain, secrets)
        assertEquals(SecurePairSave.Outcome.SAVED, store.saveZephyrPairing(proof(), "new.local", 47823, "pc"))
        plain.map["zephyrToken"] = "stale-plain"; plain.map["zephyrHost"] = "stale.local"
        Store.resetZephyrRecordForTests()
        val t = Store(plain, secrets).zephyrTarget()!!
        assertEquals(ztok, t.token); assertEquals("new.local", t.host)
    }

    @Test fun eachPairingGetsItsOwnLocalIdAndNeverTheHelpers() {
        val secrets = Prefs(); val store = Store(Prefs(), secrets)
        store.saveZephyrPairing(proof(), "h", 47823, "pc"); val a = store.zephyrTarget()!!.pcId
        store.saveZephyrPairing(proof(), "h", 47823, "pc"); val b = store.zephyrTarget()!!.pcId
        assertTrue(a != b)
    }

    @Test fun realStorePublishesTheWholeTupleOnReadAndOnEachStandaloneSetter() {
        PairingPublication.resetForTests()
        val secrets = Prefs(mapOf("helperToken" to "legacy-tok", "vlcPassword" to "pw")); val plain = Prefs(mapOf("host" to "pc.local"))
        val store = Store(plain, secrets)
        assertEquals(null, store.pairingPeek())                       // unloaded until a reader publishes
        val snap = store.helperVlcSnapshot()                          // legacy-only pairing: the fallback tuple is published, not null
        assertEquals("legacy-tok", store.pairingPeek()?.endpoint?.token)
        assertTrue(snap.sameTarget(store.pairingPeek()))
        store.vlcPort = 9090                                          // a standalone setter is its own section and republishes the whole tuple
        assertEquals(9090, store.pairingPeek()?.vlcPort)
        assertEquals("pc.local", store.pairingPeek()?.endpoint?.host)
        store.vlcPassword = "pw2"
        assertEquals("pw2", store.pairingPeek()?.vlcPassword)
        PairingPublication.resetForTests()
    }

    @Test fun aFailedSecretReadInvalidatesTheRealStoresPublishedTupleAtOnce() {
        val secrets = Prefs(mapOf("helperToken" to "tok", "vlcPassword" to "pw")); val plain = Prefs(mapOf("host" to "pc.local"))
        val store = Store(plain, secrets)
        store.helperVlcSnapshot()
        assertEquals("tok", store.pairingPeek()?.endpoint?.token)
        secrets.throwOnRead = setOf("vlcPassword")
        store.vlcPassword                                   // an unlocked getter hits the failing read and poisons the box
        assertEquals(null, store.pairingPeek())             // dropped immediately, no stale good tuple
        val after = store.helperVlcSnapshot()               // the next reader publishes the blank tuple
        assertEquals("", after.vlcPassword); assertEquals("", store.pairingPeek()?.vlcPassword)
    }

    @Test fun realStoreLegacyHostEditDropsTheMainGuardCompare() {
        val secrets = Prefs(mapOf("helperToken" to "tok", "vlcPassword" to "pw")); val plain = Prefs(mapOf("host" to "pc.local"))
        val store = Store(plain, secrets)
        val before = store.helperVlcSnapshot()
        assertFalse(before.secure)                                   // legacy-only pairing is published as plain
        assertTrue(before.samePc(store.pairingPeek()))
        store.host = "other.local"                                    // a plain host edit, same token
        assertFalse(before.samePc(store.pairingPeek()))
    }
}
