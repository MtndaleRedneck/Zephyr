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
import java.util.concurrent.TimeUnit

/** Drive the real Compose code field, actual HTTP pairing and secure persistence.
 * A green protocol test alone does not prove sixth-digit submission. */
class ZephyrAutoPairTest {
    @org.junit.After fun cleanSecurePairing() { ZephyrTestSeed.cleanup(androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().targetContext) }
    /** Failure ground truth (screenshot, hierarchy, resumed activity) captured while the app is still up, before the harness tears down. */
    @get:org.junit.Rule val failureDump = FailureDumpRule()
    /** The pairing screen is a scrolling column: a node can exist below the fold. Wait, then scroll up to 4 times, waiting after each. */
    private fun findScrolling(device: UiDevice, sel: androidx.test.uiautomator.BySelector, firstWaitMs: Long): androidx.test.uiautomator.UiObject2? {
        var node = device.wait(Until.findObject(sel), firstWaitMs)
        var swipes = 0
        while (node == null && swipes++ < 4) {
            device.swipe(device.displayWidth * 19 / 20, device.displayHeight * 3 / 4, device.displayWidth * 19 / 20, device.displayHeight / 4, 20)
            node = device.wait(Until.findObject(sel), 1500)
        }
        return node
    }
    @Test fun sixthDigitPairsExactlyOnceWithoutButtonTap() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val device = UiDevice.getInstance(instrumentation)
        val server = TlsFakePlayer.server()
        val store = Store(context)
        ZephyrTestSeed.clear(store)
        server.enqueue(MockResponse().setResponseCode(200).setBody("{\"token\":\"a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0\"}"))
        try {
            device.wakeUp()
            device.executeShellCommand("wm dismiss-keyguard")
            context.startActivity(Intent(context, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
                .putExtra("screen", "settings-pair-2"))
            // BingeMote flow: choose the PC first (typed address -> "Use this address"), then the code card appears.
            assertNotNull("address field absent", device.wait(Until.findObject(By.clazz("android.widget.EditText")), 15000))
            // The screen scans for a PC when it opens. While it scans a spinner moves the address field and button; when the scan ends the
            // layout moves back and the result message appears. No Player answers on this emulator (nothing sends or listens on discovery
            // here), so the settled state is the positive "No PC answered" message. Wait for it before typing or tapping.
            assertNotNull("opening scan did not settle (no \"No PC answered\" message)", device.wait(Until.findObject(By.textStartsWith("No PC answered")), 15000))
            val addr = device.wait(Until.findObject(By.clazz("android.widget.EditText")), 5000)
            assertNotNull("address field absent after scan", addr)
            addr!!.text = "127.0.0.1:${server.port}"
            // Re-find the button only once it is enabled (it enables when the typed address reaches the screen state), at its settled position.
            val use = device.wait(Until.findObject(By.text("Use this address").enabled(true)), 5000)
            assertNotNull("Use this address missing or not enabled", use)
            use!!.click()
            assertNotNull("code field absent", findScrolling(device, By.text("Code from your PC"), 8000))
            val fields = device.findObjects(By.clazz("android.widget.EditText"))
            assertEquals("address and code fields expected", 2, fields.size)
            fields[1].text = "12345"
            Thread.sleep(700)
            assertEquals("five digits must not submit", 0, server.requestCount)
            device.executeShellCommand("screencap -p /sdcard/zephyr-autopair-five.png")
            fields[1].text = "123456"
            val request = server.takeRequest(8, TimeUnit.SECONDS)
            assertNotNull("sixth digit did not auto-submit", request)
            assertEquals("/pair", request!!.path)
            assertEquals("123456", org.json.JSONObject(request.body.readUtf8()).getString("code"))
            val deadline = System.currentTimeMillis() + 8000
            while (store.zephyrTarget()?.token != "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0" && System.currentTimeMillis() < deadline) Thread.sleep(100)
            assertEquals("successful token not saved", "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0", store.zephyrTarget()?.token)
            // BingeMote flow: the success card sits under the form with Continue; it does not jump away.
            // The card sits under the form in a scrolling column: scroll down until it is on screen.
            var card = device.wait(Until.findObject(By.textContains("Paired with")), 3000)
            var swipes = 0
            while (card == null && swipes++ < 4) {
                device.swipe(device.displayWidth * 19 / 20, device.displayHeight * 3 / 4, device.displayWidth * 19 / 20, device.displayHeight / 4, 20)
                card = device.wait(Until.findObject(By.textContains("Paired with")), 1500)
            }
            assertNotNull("success card did not appear", card)
            val cont = findScrolling(device, By.text("Continue"), 3000)
            assertNotNull("Continue button missing", cont)
            cont!!.click()
            assertNotNull("Continue did not return to Settings", device.wait(Until.findObject(By.text("Settings")), 8000))
            device.executeShellCommand("screencap -p /sdcard/zephyr-autopair-success.png")
            Thread.sleep(700)
            var pairRequests = 1   // the one already taken
            while (true) { val r = server.takeRequest(200, TimeUnit.MILLISECONDS) ?: break; if (r.path == "/pair") pairRequests++ }   // the post-pair status check is not a pairing request
            assertEquals("one entry must create exactly one pairing request", 1, pairRequests)
        } finally {
            ZephyrTestSeed.clear(store)
            server.shutdown()
            device.pressHome()
        }
    }
}
