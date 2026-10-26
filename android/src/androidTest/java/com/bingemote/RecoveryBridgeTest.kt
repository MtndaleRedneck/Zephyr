package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import java.io.File

class RecoveryBridgeTest {
    @Test fun exact_shipped_verifier_accepts_bridge_rejects_separate() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext.createPackageContext("com.bingemote", 0)
        assertEquals(21L, ctx.packageManager.getPackageInfo("com.bingemote", 0).longVersionCode)
        ShippedAf2Updater.verify(ctx, File("/data/local/tmp/bridge.apk"))
        try {
            ShippedAf2Updater.verify(ctx, File("/data/local/tmp/separate.apk"))
            fail("Different-package Zephyr must be rejected before installer")
        } catch (e: ShippedAf2Updater.Failure) {
            assertEquals("The downloaded file is a different app.", e.msg)
        }
    }
}
