package com.bingemote

import android.content.Context
import androidx.test.platform.app.InstrumentationRegistry
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

/**
 * Repair after the secure-only migration, on the debug build (the signed-release install-over proof is the upgrade job). An old plain
 * helper pairing is retained but never counts as a connection: it reads as "pair again". A real re-pair through the production
 * SecurePairSave then makes the same store configured, keeps the legacy fields, and the production setup check connects over the
 * pinned TLS identity to a fake Player. No plaintext helper request is made at any point.
 */
class LegacyRepairAfterMigrationTest {
    @org.junit.After fun cleanSecurePairing() { ZephyrTestSeed.cleanup(InstrumentationRegistry.getInstrumentation().targetContext) }
    @get:Rule val failureDump = FailureDumpRule()

    @Test
    fun legacyPlainPairingNeedsRepairThenRealSecureRepairConnects() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        ctx.getSharedPreferences("settings", Context.MODE_PRIVATE).edit().clear().commit()
        ctx.getSharedPreferences("secrets", Context.MODE_PRIVATE).edit().clear().commit()
        ZephyrTestSeed.cleanup(ctx)
        val helperPaths = java.util.Collections.synchronizedList(ArrayList<String>())
        val helper = TlsFakePlayer.server()
        helper.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                helperPaths.add(request.path ?: "")
                return if (request.path == "/vlc/setup-status") MockResponse().setResponseCode(200).setBody("{\"configured\":true,\"pending\":false}")
                else MockResponse().setResponseCode(404).setBody("{}")
            }
        }
        val vlc = MockWebServer()
        val expected = "Basic " + android.util.Base64.encodeToString(":legacy-vlc-password".toByteArray(), android.util.Base64.NO_WRAP)
        vlc.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                if ((request.path ?: "").startsWith("/requests/status.json") && request.getHeader("Authorization") == expected) MockResponse().setResponseCode(200).setBody("{}")
                else MockResponse().setResponseCode(401).setBody("{}")
        }
        vlc.start()
        try {
            val store = Store(ctx)
            store.pcName = "Legacy PC"; store.host = "127.0.0.1"; store.helperPort = helper.port; store.vlcPort = vlc.port
            store.helperToken = "legacy-helper-token"; store.vlcPassword = "legacy-vlc-password"; store.setupDone = true
            // After the migration: the old plain pairing is kept but is not a connection.
            assertTrue("legacy plain helper pairing needs repair", store.pairingNeedsRepair)
            assertFalse("legacy plain helper pairing is not configured", store.isConfigured)
            assertEquals("no plaintext or other helper request before the repair", 0, helperPaths.size)
            // The user's real re-pair: production SecurePairSave with the pinned identity of the fake Player.
            ZephyrTestSeed.seedHelper(store, "127.0.0.1", helper.port, "legacy-vlc-password", ZephyrTestSeed.HELPER_TOKEN, TlsFakePlayer.pinHex)
            assertFalse("repaired pairing no longer needs repair", store.pairingNeedsRepair)
            assertTrue("repaired pairing is configured", store.isConfigured)
            assertEquals("VLC password carried through the repair", "legacy-vlc-password", store.vlcPassword)
            assertEquals(AutoSetupCheck.Outcome.OK, AutoSetupCheck.run(store))
            assertTrue("the setup check reached the fake Player over the pinned TLS helper", helperPaths.contains("/vlc/setup-status"))
        } finally { helper.shutdown(); vlc.shutdown() }
    }
}
