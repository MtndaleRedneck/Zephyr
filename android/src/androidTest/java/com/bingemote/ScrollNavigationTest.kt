package com.bingemote

import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

/**
 * Real taps on a real screen. Fresh entry into Settings, Feedback and Movies starts at the top; Back from a
 * sub-page returns to its parent at the position the user left it. Demo data only (the demo seed, no real PC).
 */
class ScrollNavigationTest {
    @get:Rule val failureDump = FailureDumpRule()
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val device = UiDevice.getInstance(instrumentation)

    private fun launch(screen: String) {
        device.wakeUp()
        device.executeShellCommand("wm dismiss-keyguard")
        context.startActivity(Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", screen))
    }

    private fun headerTop(): Int? = device.wait(Until.findObject(By.res("screen_header")), 8000)?.visibleBounds?.top
    private fun headerVisible(): Boolean = device.findObject(By.res("screen_header")) != null
    private fun swipeUp(times: Int) = repeat(times) {
        device.swipe(device.displayWidth / 2, device.displayHeight * 3 / 4, device.displayWidth / 2, device.displayHeight / 4, 12)
        Thread.sleep(250)
    }
    private fun tapText(t: String) {
        val n = device.wait(Until.findObject(By.textContains(t)), 8000)
        assertNotNull("'$t' not found", n); n!!.click()
    }

    /** Settings scrolled down, to Home and back in through the gear: the page opens at the top. */
    @Test fun freshEntryIntoSettingsStartsAtTheTop() {
        ZephyrTestSeed.clear(Store(context))
        launch("settings")
        val top = headerTop(); assertNotNull(top)
        swipeUp(3)
        assertFalse("settings did not scroll, test would prove nothing", headerVisible())
        device.wait(Until.findObject(By.desc("Back")), 4000)?.let { /* the header scrolled away with the content */ }
        device.pressBack(); // system Back to Home
        val gear = device.wait(Until.findObject(By.desc("Settings")), 8000); assertNotNull("Home gear", gear); gear!!.click()
        assertEquals("settings header top after fresh entry", top, headerTop())
    }

    /** Settings scrolled down, into Feedback and Back: Settings is still scrolled (not reset), and Feedback itself opened at the top. */
    @Test fun backFromFeedbackKeepsSettingsPositionAndFeedbackOpensAtTheTop() {
        ZephyrTestSeed.clear(Store(context))
        launch("settings")
        val top = headerTop(); assertNotNull(top)
        swipeUp(3)
        tapText("Send feedback")
        assertEquals("feedback header top on fresh entry", top, headerTop())
        swipeUp(2)
        device.pressBack() // Back to Settings
        assertNotNull(device.wait(Until.findObject(By.textContains("Send feedback")), 8000))
        assertFalse("Back reset Settings to the top; it must keep the position", headerVisible())
        // Entering Feedback a second time starts at the top again.
        tapText("Send feedback")
        assertEquals("feedback header top on second entry", top, headerTop())
    }

    /** Movies scrolled, to Home and in again: opens at the top. Uses the demo Movies list. */
    @Test fun freshEntryIntoMoviesStartsAtTheTop() {
        launch("movies")
        val top = headerTop(); assertNotNull(top)
        swipeUp(3)
        device.pressBack() // Back to Home
        tapText("Movies")
        assertEquals("movies header top after fresh entry", top, headerTop())
    }
}
