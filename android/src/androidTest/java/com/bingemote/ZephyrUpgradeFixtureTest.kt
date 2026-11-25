package com.bingemote

import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Real Zephyr upgrade check. The SAME file is compiled into the installed-prior-release build
 * (zephyr-android-v1.5.0) and into the new build: `seed` runs against the old install and writes a paired
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
        // The secure store must be the real one (checked on the instance that wrote the token, not a fresh open): a Keystore failure falls back to an in-memory stand-in whose token never reaches disk.
        assertEquals("secure store opened on the WRITING instance (token will be on disk)", false, s.secureStoreBroken)
        // Durable before the script installs the new build. Every setter above uses apply() (async), on TWO files: "settings"
        // (host, port, name, source, skips, haptics) and "secrets" (the paired token, through EncryptedSharedPreferences.create(context,
        // "secrets", ...), whose underlying file is context.getSharedPreferences("secrets")). commit() on each file returns only after
        // the earlier queued writes of that file are on disk and must report success. Nothing is printed.
        // No kotlin-stdlib calls here (no listOf/forEach): in the signed-upgrade job this minified test APK runs against the
        // REAL shipped 1.5.0 app, whose R8 names for stdlib classes differ from this build's (NoSuchMethodError on run 45666571505).
        assertTrue("seed commit refused for settings", ctx.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE).edit().commit())
        assertTrue("seed commit refused for secrets", ctx.getSharedPreferences("secrets", android.content.Context.MODE_PRIVATE).edit().commit())
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
        val launch = ctx.packageManager.getLaunchIntentForPackage(ctx.packageName)
        if (launch == null) throw AssertionError("no launcher intent for the app")
        launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        ctx.startActivity(launch)
        // Real arrival: the app's own window must be on screen (not just a live process).
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        assertTrue("app window never appeared after cold open", device.wait(Until.hasObject(By.pkg(ctx.packageName).depth(0)), 20000))
        Thread.sleep(3000)
        assertTrue("app window gone after cold open", device.hasObject(By.pkg(ctx.packageName).depth(0)))
        val am = ctx.getSystemService(android.content.Context.ACTIVITY_SERVICE) as android.app.ActivityManager
        // Plain loop, no Iterable.any (kotlin inline today, but one less stdlib dependency on the shipped app's shrunk classes).
        var alive = false
        for (p in am.runningAppProcesses) { if (p.processName == ctx.packageName) alive = true }
        assertTrue("app process died after cold open", alive)
        // State still intact after the app itself ran against it.
        assertEquals("upgrade-fixture-token", Store(ctx).zephyrToken)
    }
}
