package com.bingemote

import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Real Zephyr upgrade check. The SAME file is compiled into the installed-prior-release build
 * (zephyr-android-v1.4.0) and into the new build: `seed` runs against the old install and writes a paired
 * Zephyr state through the app's own Store; the new build is then installed over it with `adb install -r`
 * (no uninstall) and `verify` reads the same state back and cold-opens the app. Test-only fixture values
 * (documentation address 192.0.2.x), never the user's data. No debug hooks are used.
 */
class ZephyrUpgradeFixtureTest {
    private val ctx get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    fun seed() {
        val s = Store(ctx)
        s.zephyrHost = "192.0.2.10"
        s.zephyrPort = 47823
        s.zephyrPcName = "Upgrade Fixture PC"
        s.zephyrToken = "upgrade-fixture-token"
        s.selectedSource = Source.ZEPHYR
        s.skipMainSec = 15
        s.skipSecondarySec = 60
        s.haptics = false
        assertEquals("upgrade-fixture-token", Store(ctx).zephyrToken)
    }

    @Test
    fun verify() {
        val s = Store(ctx)
        assertEquals("192.0.2.10", s.zephyrHost)
        assertEquals(47823, s.zephyrPort)
        assertEquals("Upgrade Fixture PC", s.zephyrPcName)
        assertEquals("paired token survived the upgrade", "upgrade-fixture-token", s.zephyrToken)
        assertTrue(s.isPaired(Source.ZEPHYR))
        assertEquals(Source.ZEPHYR, s.selectedSource)
        assertEquals(15, s.skipMainSec)
        assertEquals(60, s.skipSecondarySec)
        assertEquals(false, s.haptics)
        // Cold open of the upgraded app on top of that state: it must come up and stay up.
        val launch = ctx.packageManager.getLaunchIntentForPackage(ctx.packageName)!!
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        ctx.startActivity(launch)
        Thread.sleep(6000)
        val am = ctx.getSystemService(android.content.Context.ACTIVITY_SERVICE) as android.app.ActivityManager
        assertTrue("app process died after cold open", am.runningAppProcesses.any { it.processName == ctx.packageName })
        // State still intact after the app itself ran against it.
        assertEquals("upgrade-fixture-token", Store(ctx).zephyrToken)
    }
}
