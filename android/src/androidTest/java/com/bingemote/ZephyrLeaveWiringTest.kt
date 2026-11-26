package com.bingemote

import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.*
import org.junit.Test

/** Real Back and Cancel wiring on the first-run Player pairing screen (not the pure predicate): what the store holds and where the app lands. */
class ZephyrLeaveWiringTest {
    private fun scrollFind(device: UiDevice, sel: androidx.test.uiautomator.BySelector): androidx.test.uiautomator.UiObject2? {
        var n = device.wait(Until.findObject(sel), 8000); var i = 0
        while (n == null && i++ < 4) {
            device.swipe(device.displayWidth * 19 / 20, device.displayHeight * 3 / 4, device.displayWidth * 19 / 20, device.displayHeight / 4, 20)
            n = device.wait(Until.findObject(sel), 1500)
        }
        return n
    }

    /** First run, pair succeeds, status check answers [statusCode]; then leave by [useBack] (system Back) or the Cancel button. */
    private fun run(statusCode: Int, useBack: Boolean, expectFinish: Boolean) {
        val inst = InstrumentationRegistry.getInstrumentation()
        val context = inst.targetContext
        val device = UiDevice.getInstance(inst)
        val server = MockWebServer(); server.start()
        val store = Store(context)
        store.zephyrToken = ""; store.helperToken = ""; store.setupDone = false; store.selectedSource = Source.VLC
        store.zephyrHost = "127.0.0.1"; store.zephyrPort = server.port
        server.enqueue(MockResponse().setResponseCode(200).setBody("{\"token\":\"leave-test-token\"}"))
        server.enqueue(MockResponse().setResponseCode(statusCode).setBody("{}"))
        try {
            device.wakeUp(); device.executeShellCommand("wm dismiss-keyguard")
            context.startActivity(Intent(context, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK).putExtra("screen", "setup-zephyr"))
            // first run opens at step 1 (install); go to the pair step
            val next = device.wait(Until.findObject(By.text("Zephyr Player is running")), 15000)
            assertNotNull("install step missing", next); next!!.click()
            val addr = device.wait(Until.findObject(By.clazz("android.widget.EditText")), 15000)
            assertNotNull("address field absent", addr)
            addr!!.text = "127.0.0.1:${server.port}"
            device.wait(Until.findObject(By.text("Use this address")), 5000)!!.click()
            assertNotNull("code field absent", scrollFind(device, By.text("Code from your PC")))
            device.findObjects(By.clazz("android.widget.EditText"))[1].text = "123456"
            val deadline = System.currentTimeMillis() + 8000
            while (store.zephyrToken != "leave-test-token" && System.currentTimeMillis() < deadline) Thread.sleep(100)
            assertEquals("token not saved", "leave-test-token", store.zephyrToken)
            // wait for the check to finish (a result card replaces Checking)
            val resultSel = if (statusCode == 401) By.text("Pair again") else if (statusCode == 200) By.text("Done") else By.text("Try again")
            assertNotNull("check result not shown", scrollFind(device, resultSel))
            if (useBack) {
                // First-run Back from the pair step first walks back to the install step (existing behavior); a second Back leaves.
                device.pressBack(); Thread.sleep(700)
                if (device.findObject(By.text("Zephyr Player is running")) != null) { device.pressBack() }
            } else {
                // Cancel sits at the top of the scrolling column: scroll up until it is on screen.
                var cancel = device.findObject(By.text("Cancel")); var up = 0
                while (cancel == null && up++ < 8) {
                    device.swipe(device.displayWidth * 19 / 20, device.displayHeight / 4, device.displayWidth * 19 / 20, device.displayHeight * 3 / 4, 20)
                    cancel = device.wait(Until.findObject(By.text("Cancel")), 1000)
                }
                assertNotNull("Cancel button not found", cancel); cancel!!.click()
            }
            Thread.sleep(1500)
            assertEquals("setupDone after leaving", expectFinish, store.setupDone)
            if (expectFinish) {
                assertEquals(Source.ZEPHYR, store.selectedSource)
                assertNotNull("must land on Home (Settings icon)", device.wait(Until.findObject(By.desc("Settings")), 5000))
                assertNull("must not land on the Settings screen", device.findObject(By.text("Settings")))
            } else {
                assertEquals("source must be untouched", Source.VLC, store.selectedSource)
                assertNotNull("plain leave goes to Settings", device.wait(Until.findObject(By.text("Settings")), 5000))
            }
        } finally {
            store.zephyrToken = ""; store.setupDone = false; store.selectedSource = Source.VLC
            server.shutdown(); device.pressHome()
        }
    }

    @Test fun backAfterCouldntCheckFinishesFirstRun() = run(500, useBack = true, expectFinish = true)
    @Test fun cancelAfterCouldntCheckFinishesFirstRun() = run(500, useBack = false, expectFinish = true)
    @Test fun backAfterRejectedTokenLeavesStateUnchanged() = run(401, useBack = true, expectFinish = false)
    @Test fun cancelAfterRejectedTokenLeavesStateUnchanged() = run(401, useBack = false, expectFinish = false)
}
