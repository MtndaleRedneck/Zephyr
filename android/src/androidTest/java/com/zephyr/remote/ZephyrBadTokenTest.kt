package com.zephyr.remote

import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.AndroidJUnit4
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import com.bingemote.RemoteState
import com.bingemote.Source
import com.bingemote.Store
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

private const val WIRE_HOST = "10.0.2.2"
private const val PAIR_AGAIN_MARKER = "paired with the PC anymore"

private fun wireArgs(): Pair<Int, String> {
    val args = InstrumentationRegistry.getArguments()
    val port = args.getString("wire_port")?.toIntOrNull() ?: 0
    val code = args.getString("pair_code") ?: ""
    assumeTrue("wire host not wired (needs -e wire_port N -e pair_code NNNNNN)", port > 0 && code.isNotEmpty())
    return port to code
}

private fun launchApp(device: UiDevice, pkg: String) {
    // No force-stop: the instrumentation runs INSIDE the app process - stopping our
    // own package kills this session. am instrument already force-stops the app
    // before each class (see SeekGestureTest), so every launch here is a cold start.
    device.wakeUp()
    device.executeShellCommand("wm dismiss-keyguard")
    device.executeShellCommand("am start -n $pkg/com.bingemote.MainActivity")
}

/**
 * BadToken integration, phase A (fresh wire host): pair for real, seed the app's
 * own Store exactly the way ZephyrSetupScreen's success path does, then prove
 * the baseline - a remote command (control/status) and a library browse both
 * succeed over the real wire, and the app shows NO pair-again notice.
 */
@RunWith(AndroidJUnit4::class)
class ZephyrBadTokenSeedTest {
    @Before fun wired() { wireArgs() }

    @Test fun pairSeedAndBaseline() {
        val (port, code) = wireArgs()
        val token = ZephyrClient.pair(WIRE_HOST, port, code)
        assertTrue("empty token", token.isNotEmpty())

        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = ctx.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        prefs.edit()
            .putBoolean("setupDone", true)
            .putString("selectedSource", "zephyr")
            .putString("zephyrHost", WIRE_HOST)
            .putInt("zephyrPort", port)
            .putString("zephyrPcName", "Wire PC")
            .commit()
        val store = Store(ctx)
        store.zephyrToken = token
        assertTrue("seeded token unreadable", Store(ctx).zephyrToken == token)

        // Baseline at the wire: remote command + browse succeed with the live token.
        ZephyrClient.controlStatus(WIRE_HOST, port, token)
        val (listed, _) = ZephyrClient.libraryList(WIRE_HOST, port, token)
        assertTrue("baseline browse failed", listed)

        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        launchApp(device, ctx.packageName)
        Thread.sleep(8000)
        assertNull("pair-again notice shown while the pairing is valid",
            device.findObject(By.textContains(PAIR_AGAIN_MARKER)))
        assertTrue("app not running after launch",
            device.executeShellCommand("pidof ${ctx.packageName}").isNotBlank())
    }
}

/**
 * BadToken integration, phase B1: deterministic per-path proof, in-process, no UI
 * races. The harness restarted the wire host, so the phase-A token is revoked
 * server-side (the same 401 the PC's remove-phone produces; removal itself is a
 * Windows-side UI action covered there - this suite does NOT exercise the PC's
 * remove-phone control). Each leg starts independently from a FRESH RemoteState
 * (unpaired=false) with the seeded paired Store, so no leg's result is produced
 * or masked by another leg:
 *  - command path: the queued command loop's own BadToken catch sets unpaired.
 *  - status-poll path: pollOnce's catch sets unpaired.
 *  - browse path: documents CURRENT behavior - the wire call raises BadToken, and
 *    (MoviesScreen.loadZephyr, unlike the helper browse path) sets NO unpaired
 *    state; the user sees a typed inline pair-again message instead.
 */
@RunWith(AndroidJUnit4::class)
class ZephyrBadTokenPathsTest {
    @Before fun wired() { wireArgs() }

    @Test fun commandPollAndBrowsePathsIndependently() {
        val (port, _) = wireArgs()
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val store = Store(ctx)
        assertTrue("phase A seeding missing (no zephyr token)", store.zephyrToken.isNotEmpty())
        assertTrue("phase A seeding missing (not zephyr source)", store.selectedSource == Source.ZEPHYR)
        val revoked = store.zephyrToken

        // Precondition: the seeded token is really revoked server-side.
        try {
            ZephyrClient.controlStatus(WIRE_HOST, port, revoked)
            fail("revoked token still accepted by the wire host")
        } catch (e: ZephyrClient.BadToken) { /* expected */ }

        // COMMAND path: fresh RemoteState (command loop runs from init; NO poll -
        // start() is never called), a real user-style command hits the 401 and
        // the command loop's own catch flips unpaired. Nothing else can have.
        val cmdScope = CoroutineScope(Dispatchers.IO + Job())
        try {
            val rs = RemoteState(ctx, store, cmdScope)
            assertFalse("command leg did not start from paired state", rs.unpaired)
            rs.toggleFullscreen()
            val deadline = System.currentTimeMillis() + 10_000
            while (!rs.unpaired && System.currentTimeMillis() < deadline) Thread.sleep(100)
            assertTrue("command-path 401 did not surface pair-again state", rs.unpaired)
        } finally { cmdScope.cancel() }

        // STATUS-POLL path: another fresh RemoteState; pollOnce alone hits the 401.
        val pollScope = CoroutineScope(Dispatchers.IO + Job())
        try {
            val rs = RemoteState(ctx, store, pollScope)
            assertFalse("poll leg did not start from paired state", rs.unpaired)
            runBlocking { rs.pollOnce() }
            assertTrue("status-poll 401 did not surface pair-again state", rs.unpaired)
        } finally { pollScope.cancel() }

        // BROWSE path (current-behavior pin): the same call MoviesScreen.loadZephyr
        // makes raises BadToken, and a RemoteState involved in that flow keeps
        // unpaired=false - loadZephyr's catch only maps the message. Contrast with
        // the two legs above; the helper browse path DOES flip state via
        // remote.onHelperBadToken() (MoviesScreen.kt:311).
        val browseScope = CoroutineScope(Dispatchers.IO + Job())
        try {
            val rs = RemoteState(ctx, store, browseScope)
            try {
                ZephyrClient.libraryList(WIRE_HOST, port, revoked)
                fail("browse with a revoked token did not fail")
            } catch (e: ZephyrClient.BadToken) {
                assertTrue("BadToken carries the pair-again wording",
                    (e.message ?: "").contains("Pair again"))
            }
            assertFalse("browse-path pin changed: loadZephyr now flips unpaired (update this pin and the review notes)",
                rs.unpaired)
        } finally { browseScope.cancel() }
    }
}

/**
 * BadToken integration, phase B2 (UI, real screen): with the revoked pairing,
 * the app itself surfaces the pair-again notice - no raw wire error - including
 * across a Movies browse; then re-pair with the fresh code through the app's own
 * save path (ZephyrSetupScreen: ZephyrClient.pair then Store) and verify recovery.
 */
@RunWith(AndroidJUnit4::class)
class ZephyrBadTokenAfterRemovalTest {
    @Before fun wired() { wireArgs() }

    @Test fun removedPairingShowsPairAgainThenRepairs() {
        val (port, newCode) = wireArgs()
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val store = Store(ctx)
        assertTrue("phase A seeding missing (no zephyr token)", store.zephyrToken.isNotEmpty())
        assertTrue("phase A seeding missing (not zephyr source)", store.selectedSource == Source.ZEPHYR)

        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        launchApp(device, ctx.packageName)

        // The app's own status poll (remote command) meets the 401 first-hand.
        val notice = device.wait(Until.findObject(By.textContains(PAIR_AGAIN_MARKER)), 30_000)
        assertNotNull("pair-again notice never appeared after the pairing was revoked", notice)
        assertNull("raw wire error leaked to the screen",
            device.findObject(By.textContains("Player returned 401")))

        // Browse through the UI: opening Movies fires a real library call against
        // the revoked pairing; the typed pair-again wording shows, no raw error.
        device.findObject(By.text("Movies"))?.let { movies ->
            movies.click()
            Thread.sleep(5000)
            assertNull("raw wire error leaked to the screen after browse",
                device.findObject(By.textContains("Player returned 401")))
            device.pressBack()
            Thread.sleep(1500)
        }

        // Pair again with the new code through the same calls the setup screen
        // makes (ZephyrSetupScreen: ZephyrClient.pair then Store save). The UI
        // recovery proof runs in ZephyrBadTokenRepairedTest: am instrument
        // force-stops the app between classes, so that session is a cold start
        // with the new token - a mere re-launch here would keep the app's old
        // RemoteState (and its stale token) alive.
        val newToken = ZephyrClient.pair(WIRE_HOST, port, newCode)
        assertTrue("re-pair returned empty token", newToken.isNotEmpty())
        store.zephyrToken = newToken
        assertTrue("re-paired token unreadable", Store(ctx).zephyrToken == newToken)
        ZephyrClient.controlStatus(WIRE_HOST, port, newToken)
        val (listed, _) = ZephyrClient.libraryList(WIRE_HOST, port, newToken)
        assertTrue("browse failed on the wire after re-pairing", listed)
    }
}

/**
 * BadToken integration, phase B3 (UI recovery): fresh instrumented session, so
 * the app cold-starts with the re-paired token from phase B2. The pair-again
 * notice must be GONE and the app healthy on the main screen.
 */
@RunWith(AndroidJUnit4::class)
class ZephyrBadTokenRepairedTest {
    @Test fun repairedPairingShowsNoNotice() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val store = Store(ctx)
        assertTrue("phase B2 re-pairing missing (no zephyr token)", store.zephyrToken.isNotEmpty())
        assertTrue("phase A seeding missing (not zephyr source)", store.selectedSource == Source.ZEPHYR)

        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        launchApp(device, ctx.packageName)
        Thread.sleep(8000)
        assertNull("pair-again notice still shown after re-pairing",
            device.findObject(By.textContains(PAIR_AGAIN_MARKER)))
        assertTrue("app not running after re-pair launch",
            device.executeShellCommand("pidof ${ctx.packageName}").isNotBlank())
    }
}
