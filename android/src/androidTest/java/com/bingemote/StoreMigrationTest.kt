package com.bingemote

import android.content.Context
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.assertFalse
import org.junit.Rule
import org.junit.Test

/** 1.9 settings migrations on existing installs: the removed toggles leave no
 * stale keys, and the retired cancel-style setting is removed. */
class StoreMigrationTest {
    @org.junit.After fun cleanSecurePairing() { ZephyrTestSeed.cleanup(androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().targetContext) }
    @get:Rule val failureDump = FailureDumpRule()

    @Test
    fun encryptedStoreRetains64HexTokenAcrossReopen() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val token = "a7".repeat(32)
        ZephyrTestSeed.seed(Store(ctx), "192.0.2.10", 47823, token = token)
        Store.resetZephyrRecordForTests()   // the reopen must come from storage, not the process cache
        assertEquals(token, Store(ctx).zephyrTarget()?.token)
        org.junit.Assert.assertFalse(Store(ctx).secureStoreBroken)
        ZephyrTestSeed.clear(Store(ctx))
    }

    @Test
    fun removedTogglesAndCancelStyleAreCleared() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = ctx.getSharedPreferences("settings", Context.MODE_PRIVATE)
        // Hermetic: other tests exercise the cancel-style rig extra, which
        // persists a real value; the default check needs a clean file.
        prefs.edit().clear().commit()
        prefs.edit().putBoolean("seekOnTop", false).putBoolean("autoReconnect", false).putString("cancelStyle", "ring").commit()
        Store(ctx) // construction runs the migration
        assertFalse("stale seekOnTop key survived", prefs.contains("seekOnTop"))
        assertFalse("stale autoReconnect key survived", prefs.contains("autoReconnect"))
        assertFalse("stale cancelStyle key survived", prefs.contains("cancelStyle"))
    }

    /** v2.0 in-place upgrade continuity (same applicationId + signing key): an
     * existing paired install keeps its helper pairing and identity through the
     * Connected Sources migration, defaults to the VLC source, starts unpaired
     * for Zephyr, and leaves any plain-pref zephyrToken unread (NeedsRepair). */
    @Test
    fun pairedInstallSurvivesConnectedSourcesMigration() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = ctx.getSharedPreferences("settings", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        // Hermetic: an earlier test in this class saves a real (encrypted) Zephyr pairing that clearing "settings" does not remove.
        // The expectation below, unpaired with an old plain token left alone, is about a genuine old install, so start from none.
        ZephyrTestSeed.clear(Store(ctx))
        prefs.edit()
            .putString("pcName", "DESKTOP-TEST")
            .putString("host", "192.168.1.50")
            .putString("zephyrToken", "legacy-plain-token") // v13-dev plain storage
            .commit()
        val store = Store(ctx)
        store.helperToken = "helper-secret"
        val store2 = Store(ctx) // second construction = post-migration state
        assertEquals("DESKTOP-TEST", store2.pcName)
        assertEquals("192.168.1.50", store2.host)
        assertEquals("helper-secret", store2.helperToken)
        assertEquals(Source.VLC, store2.selectedSource)
        // Secure-only: an old plain token is never read or migrated; it needs a new pairing and is left on disk untouched.
        Store.resetZephyrRecordForTests()
        assertEquals(null, Store(ctx).zephyrTarget())
        assertTrue(Store(ctx).zephyrPairing() === ZephyrCredential.State.NeedsRepair)
        assertTrue("old plain token is left in place", prefs.contains("zephyrToken"))
        prefs.edit().clear().commit()
    }
}
