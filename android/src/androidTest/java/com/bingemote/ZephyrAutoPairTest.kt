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
    @Test fun sixthDigitPairsExactlyOnceWithoutButtonTap() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val device = UiDevice.getInstance(instrumentation)
        val server = MockWebServer()
        server.start()
        val store = Store(context)
        store.zephyrToken = ""
        store.zephyrHost = "127.0.0.1"
        store.zephyrPort = server.port
        server.enqueue(MockResponse().setResponseCode(200).setBody("{\"token\":\"autopair-test-token\"}"))
        try {
            device.wakeUp()
            device.executeShellCommand("wm dismiss-keyguard")
            context.startActivity(Intent(context, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
                .putExtra("screen", "settings-pair-2"))
            // BingeMote flow: choose the PC first (typed address -> "Use this address"), then the code card appears.
            val addr = device.wait(Until.findObject(By.clazz("android.widget.EditText")), 15000)
            assertNotNull("address field absent", addr)
            addr!!.text = "127.0.0.1:${server.port}"
            val use = device.wait(Until.findObject(By.text("Use this address")), 5000)
            assertNotNull("Use this address missing", use)
            use!!.click()
            assertNotNull("code field absent", device.wait(Until.findObject(By.text("Code from your PC")), 8000))
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
            while (store.zephyrToken != "autopair-test-token" && System.currentTimeMillis() < deadline) Thread.sleep(100)
            assertEquals("successful token not saved", "autopair-test-token", store.zephyrToken)
            // BingeMote flow: the success card sits under the form with Continue; it does not jump away.
            // The card sits under the form in a scrolling column: scroll down until it is on screen.
            var card = device.wait(Until.findObject(By.textContains("Paired with")), 3000)
            var swipes = 0
            while (card == null && swipes++ < 4) {
                device.swipe(device.displayWidth / 2, device.displayHeight * 3 / 4, device.displayWidth / 2, device.displayHeight / 4, 20)
                card = device.wait(Until.findObject(By.textContains("Paired with")), 1500)
            }
            assertNotNull("success card did not appear", card)
            val cont = device.wait(Until.findObject(By.text("Continue")), 3000)
            assertNotNull("Continue button missing", cont)
            cont!!.click()
            assertNotNull("Continue did not return to Settings", device.wait(Until.findObject(By.text("Settings")), 8000))
            device.executeShellCommand("screencap -p /sdcard/zephyr-autopair-success.png")
            Thread.sleep(700)
            assertEquals("one entry must create exactly one pairing request", 1, server.requestCount)
        } finally {
            store.zephyrToken = ""
            server.shutdown()
            device.pressHome()
        }
    }
}
