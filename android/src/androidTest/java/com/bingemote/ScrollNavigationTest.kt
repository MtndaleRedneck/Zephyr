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

    /** Movies scrolled, to Home and in again: opens at the top. Demo Movies list of 43 rows (extra "manymovies", debug only), Home paired demo. */
    @Test fun freshEntryIntoMoviesStartsAtTheTop() {
        ZephyrTestSeed.clear(Store(context))
        launchWith("main", mapOf("pcdemo" to true, "demopaired" to true, "demostatus" to true, "statuscase" to "cur-ok", "manymovies" to true))
        val movies = device.wait(Until.findObject(By.desc("Movies")), 12000); assertNotNull("Home Movies tile", movies); movies!!.click()
        val listShown = waitRow("The Big Example Movie", 12000)
        shot("hg-sn-movies-1-after-tap")
        assertNotNull("demo list did not appear", listShown)
        assertNotNull("top of the list: first folder row", row("Documentaries"))
        swipeUp(4)
        shot("hg-sn-movies-2-after-swipe")
        assertNull("list did not scroll (first folder row still visible): the test would prove nothing", row("Documentaries"))
        device.pressBack() // Back to Home
        val again = device.wait(Until.findObject(By.desc("Movies")), 8000); assertNotNull("Home Movies tile after Back", again); again!!.click()
        shot("hg-sn-movies-3-reentered")
        assertNotNull("Movies reopened but is not at the top (first folder row missing)", waitRow("Documentaries", 8000))
    }

    /** A list row by its visible name. Rows are merged clickable nodes, so the name can sit in text or in content description; match either, as a substring. */
    private fun row(name: String) = device.findObject(By.textContains(name)) ?: device.findObject(By.descContains(name))
    private fun waitRow(name: String, ms: Long): androidx.test.uiautomator.UiObject2? {
        val end = System.currentTimeMillis() + ms
        while (System.currentTimeMillis() < end) { row(name)?.let { return it }; Thread.sleep(250) }
        return null
    }

    /** Evidence picture pulled by ui-evidence.sh (hg-*.png on /sdcard). */
    private fun shot(name: String) { device.executeShellCommand("screencap -p /sdcard/$name.png") }

    /** Same as launch(), with demo extras. No force-stop: this test runs inside the app's own process, so stopping the package would kill the instrumentation. */
    private fun launchWith(screen: String, extras: Map<String, Any>) {
        device.wakeUp()
        device.executeShellCommand("wm dismiss-keyguard")
        val i = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", screen)
        extras.forEach { (k, v) -> when (v) { is Boolean -> i.putExtra(k, v); is String -> i.putExtra(k, v) } }
        context.startActivity(i)
    }
}
