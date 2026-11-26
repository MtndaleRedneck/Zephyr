package com.bingemote

import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.AndroidJUnit4
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import org.junit.After
import org.junit.Assert.assertNotNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The Home card for an unpaired selected source opens that source's pairing screen directly
 * (not generic Settings), and Cancel returns to Home. Real activity, real taps.
 */
@RunWith(AndroidJUnit4::class)
class UnpairedCardRoutingTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val device = UiDevice.getInstance(instrumentation)

    private lateinit var saved: List<Any>

    @Before fun saveStore() {
        val s = Store(context)
        saved = listOf(s.setupDone, s.selectedSource, s.zephyrToken, s.host, s.vlcPassword)
    }

    /** Leave the app's saved state exactly as found so later classes see what they expect. */
    @After fun restoreStore() {
        val s = Store(context)
        s.setupDone = saved[0] as Boolean
        s.selectedSource = saved[1] as Source
        s.zephyrToken = saved[2] as String
        s.host = saved[3] as String
        s.vlcPassword = saved[4] as String
        device.executeShellCommand("am force-stop ${context.packageName}")
    }

    private fun home() {
        device.wakeUp()
        device.executeShellCommand("wm dismiss-keyguard")
        context.startActivity(Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", "main"))
    }

    @Test fun zephyrCardOpensZephyrPairingAndCancelReturnsHome() {
        val store = Store(context)
        store.setupDone = true
        store.selectedSource = Source.ZEPHYR
        store.zephyrToken = ""
        home()
        val card = device.wait(Until.findObject(By.text("Zephyr PC isn't paired")), 15000)
        assertNotNull("unpaired Zephyr card not shown", card)
        device.executeShellCommand("screencap -p /sdcard/unpaired-card-before.png")
        card!!.click()
        assertNotNull("Zephyr pairing screen not opened by the card", device.wait(Until.findObject(By.text("Set up Zephyr Player")), 10000))
        device.executeShellCommand("screencap -p /sdcard/unpaired-card-pairing.png")
        device.wait(Until.findObject(By.text("Cancel")), 5000)!!.click()
        assertNotNull("Cancel did not return to Home", device.wait(Until.findObject(By.text("Zephyr PC isn't paired")), 10000))
        device.executeShellCommand("screencap -p /sdcard/unpaired-card-back-home.png")
    }

    @Test fun systemBackFromCardPairingReturnsHomeAndSettingsPairingStillReturnsToSettings() {
        val store = Store(context)
        store.setupDone = true
        store.selectedSource = Source.ZEPHYR
        store.zephyrToken = ""
        home()
        device.wait(Until.findObject(By.text("Zephyr PC isn't paired")), 15000)!!.click()
        assertNotNull(device.wait(Until.findObject(By.text("Set up Zephyr Player")), 10000))
        device.pressBack()
        assertNotNull("system Back did not return to Home", device.wait(Until.findObject(By.text("Zephyr PC isn't paired")), 10000))
        // Now the Settings route: the flag must not be stale, Cancel goes to Settings.
        device.wait(Until.findObject(By.desc("Settings")), 5000)!!.click()
        assertNotNull("Settings not opened", device.wait(Until.findObject(By.text("Settings")), 10000))
        device.wait(Until.findObject(By.text("Zephyr PC")), 5000)!!.click()
        assertNotNull("Zephyr pairing not opened from Settings", device.wait(Until.findObject(By.text("Set up Zephyr Player")), 10000))
        device.wait(Until.findObject(By.text("Cancel")), 5000)!!.click()
        assertNotNull("Cancel from the Settings route must return to Settings, not Home", device.wait(Until.findObject(By.text("Connected Sources")), 10000))
    }

    @Test fun vlcCardOpensVlcPairing() {
        val store = Store(context)
        store.setupDone = true
        store.selectedSource = Source.VLC
        store.host = ""
        store.vlcPassword = ""
        home()
        val card = device.wait(Until.findObject(By.text("VLC PC isn't paired")), 15000)
        assertNotNull("unpaired VLC card not shown", card)
        card!!.click()
        assertNotNull("VLC pairing screen not opened by the card", device.wait(Until.findObject(By.text("Set up Zephyr VLC Remote")), 10000))
    }
}
