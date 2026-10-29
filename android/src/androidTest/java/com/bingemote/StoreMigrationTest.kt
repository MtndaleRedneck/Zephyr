package com.bingemote

import android.content.Context
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

/** 1.9 settings migrations on existing installs: the removed toggles leave no
 * stale keys, and the new cancel-style setting defaults to Ring only. */
class StoreMigrationTest {
    @Test
    fun encryptedStoreRetains64HexTokenAcrossReopen() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val token = "a7".repeat(32)
        Store(ctx).zephyrToken = token
        assertEquals(token, Store(ctx).zephyrToken)
        org.junit.Assert.assertFalse(Store(ctx).secureStoreBroken)
    }

    @Test
    fun removedTogglesAreClearedAndStyleDefaultsToRing() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = ctx.getSharedPreferences("settings", Context.MODE_PRIVATE)
        // Hermetic: other tests exercise the cancel-style rig extra, which
        // persists a real value; the default check needs a clean file.
        prefs.edit().clear().commit()
        prefs.edit().putBoolean("seekOnTop", false).putBoolean("autoReconnect", false).commit()
        Store(ctx) // construction runs the migration
        assertFalse("stale seekOnTop key survived", prefs.contains("seekOnTop"))
        assertFalse("stale autoReconnect key survived", prefs.contains("autoReconnect"))
        assertEquals("ring", Store(ctx).cancelStyle)
    }

    /** v2.0 in-place upgrade continuity (same applicationId + signing key): an
     * existing paired install keeps its helper pairing and identity through the
     * Connected Sources migration, defaults to the VLC source, starts unpaired
     * for Zephyr, and moves any plain-pref zephyrToken into encrypted secrets. */
    @Test
    fun pairedInstallSurvivesConnectedSourcesMigration() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = ctx.getSharedPreferences("settings", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
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
        assertEquals("legacy-plain-token", store2.zephyrToken)
        assertFalse("plain zephyrToken key must be migrated out", prefs.contains("zephyrToken"))
        prefs.edit().clear().commit()
    }
}
