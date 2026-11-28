package com.bingemote

import android.content.Intent
import android.util.Log
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
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
        // Legacy on-disk layout written directly (the secure-only build has no setter for it): host, port, name and a plain token.
        assertTrue(ctx.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE).edit()
            .putString("zephyrHost", "192.0.2.10").putInt("zephyrPort", 47823).putString("zephyrPcName", "Upgrade Fixture PC")
            .putString("zephyrToken", "upgrade-fixture-token").commit())
        s.selectedSource = Source.ZEPHYR
        s.skipMainSec = 15
        s.skipSecondarySec = 60
        s.haptics = false
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
        // Secure-only upgrade: the old plain pairing is not usable (needs a new pairing) and nothing was deleted or read as a fallback.
        assertEquals("Upgrade Fixture PC", s.zephyrPcName)
        assertEquals(null, s.zephyrTarget())
        assertTrue(s.zephyrPairing() === ZephyrCredential.State.NeedsRepair)
        // The legacy token must be retained exactly: every encrypted entry the old app left in "secrets" (snapshot taken by the old-tree
        // seed) is still present with identical bytes. An unrelated or new key cannot satisfy this; a deleted or rewritten token fails it.
        assertLegacySecretsRetained()
        assertFalse(s.isPaired(Source.ZEPHYR))
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
        // The criterion is unchanged (the app's own window, same query, same 20 s budget, same assert). Run 46113470197 showed the app
        // resumed and drawn while the emulator's "Pixel Launcher isn't responding" system dialog took focus and hid the app from
        // UiAutomator's query. AnrDialogGate tolerates exactly that one dialog (captured, then Wait tapped); any other "isn't responding"
        // dialog, including one for this app, fails the test explicitly and is never dismissed.
        val windowUp = waitForAppWindow(device, 20000)
        // Diagnostic only, BEFORE the assertion fails and the instrumentation tears the app down: what the screen, the focus
        // and the accessibility tree looked like. The pass/fail criterion below is unchanged (same query, same 20 s, same assert).
        if (!windowUp) captureWindowFailure(device)
        assertTrue("app window never appeared after cold open", windowUp)
        Thread.sleep(3000)
        assertTrue("app window gone after cold open", appWindowPersists(device))
        val am = ctx.getSystemService(android.content.Context.ACTIVITY_SERVICE) as android.app.ActivityManager
        // Plain loop, no Iterable.any (kotlin inline today, but one less stdlib dependency on the shipped app's shrunk classes).
        var alive = false
        val procs = am.runningAppProcesses
        if (procs == null) throw AssertionError("runningAppProcesses returned null")
        for (p in procs) { if (p.processName.equals(ctx.packageName)) alive = true }
        assertTrue("app process died after cold open", alive)
        // State still intact after the app itself ran against it.
        assertTrue(Store(ctx).zephyrPairing() === ZephyrCredential.State.NeedsRepair)
    }

    // Reads the entries of the encrypted prefs file (name -> stored ciphertext) without decrypting; Java file APIs only.
    private fun secretEntries(bytes: ByteArray): java.util.HashMap<String, String> {
        val out = java.util.HashMap<String, String>()
        val m = java.util.regex.Pattern.compile("<string name=\"([^\"]+)\">([^<]*)</string>").matcher(String(bytes, java.nio.charset.StandardCharsets.UTF_8))
        while (m.find()) out.put(m.group(1), m.group(2))
        return out
    }

    private fun assertLegacySecretsRetained() {
        val snap = java.io.File(ctx.filesDir, "seed-secrets.xml").toPath()
        assertTrue("seed snapshot of the encrypted secrets file is missing", java.nio.file.Files.exists(snap))
        val before = secretEntries(java.nio.file.Files.readAllBytes(snap))
        assertTrue("seed left no encrypted entry (the legacy token)", before.size > 0)
        val now = secretEntries(java.nio.file.Files.readAllBytes(java.io.File(ctx.dataDir, "shared_prefs/secrets.xml").toPath()))
        val it = before.entries.iterator()
        while (it.hasNext()) {
            val e = it.next()
            assertEquals("encrypted secret entry changed or removed by the upgrade", e.value, now.get(e.key))
        }
    }

    // Diagnostic only; never changes the verdict. No kotlin stdlib calls (the shipped release app shrinks them): Java APIs, int
    // loops, string + concatenation, no ==, no templates, no `!!`, no extensions. The whole capture is one try/catch so that
    // nothing here (context lookup, files dir, IO) can throw past the caller; the assert stays outside, in verify().
    private val taps = AnrDialogGate.Taps()

    /** Null when no "isn't responding" node is on screen; a synthetic dialog the gate rejects when there is more than one candidate. */
    private fun foundDialog(device: UiDevice): AnrDialogGate.Dialog? {
        val nodes = device.findObjects(By.textContains("isn't responding"))
        if (nodes.isEmpty()) return null
        if (nodes.size > 1) return AnrDialogGate.Dialog("${nodes.size} ANR dialogs on screen", null)
        return try { AnrDialogGate.Dialog(nodes[0].text, nodes[0].applicationPackage) }
        catch (e: androidx.test.uiautomator.StaleObjectException) { null }
    }

    /**
     * MANDATORY evidence before any tolerated dismissal: screenshot, window hierarchy and a log line must all be written and non-empty,
     * else this throws and the gate never reaches the tap.
     */
    private fun captureDialog(device: UiDevice, n: Int, d: AnrDialogGate.Dialog) {
        val dir = ctx.getExternalFilesDir(null) ?: throw AssertionError("cannot capture the ANR dialog: no external files dir")
        Log.i("ZephyrUpgradeFixture", "tolerated system ANR dialog #$n: title=${d.title} pkg=${d.pkg}")
        val png = java.io.File(dir, "bm-andialog-upgrade-verify-$n-before.png")
        if (!device.takeScreenshot(png) || !png.exists() || png.length() == 0L) throw AssertionError("cannot capture the ANR dialog: screenshot failed")
        val uix = java.io.File(dir, "bm-andialog-upgrade-verify-$n-before.uix")
        try { device.dumpWindowHierarchy(uix) } catch (e: java.io.IOException) { throw AssertionError("cannot capture the ANR dialog: hierarchy dump failed: ${e.message}") }
        if (!uix.exists() || uix.length() == 0L) throw AssertionError("cannot capture the ANR dialog: hierarchy file empty")
        val log = java.io.File(dir, "bm-dismissed-dialogs.txt")
        try { log.appendText("test=upgrade-verify n=$n title=${d.title} pkg=${d.pkg}\n") } catch (e: java.io.IOException) { throw AssertionError("cannot capture the ANR dialog: log write failed: ${e.message}") }
    }

    /**
     * Re-reads the dialog node right before the click and taps only the Wait button inside THAT dialog's own container (nearest ancestor
     * of its title node that holds a Wait), never a global By.text("Wait"). Any stale, changed, second or ambiguous dialog throws.
     * UiAutomator has no atomic click-if-unchanged, so a replacement in the few ms between this re-read and the click is not excluded.
     */
    private fun tapWaitInsideDialog(device: UiDevice, first: AnrDialogGate.Dialog) {
        val nodes = device.findObjects(By.textContains("isn't responding"))
        val node = nodes.firstOrNull()
        val again = try { node?.let { AnrDialogGate.Dialog(it.text, it.applicationPackage) } }
            catch (e: androidx.test.uiautomator.StaleObjectException) { null }
        var container = try { node?.parent } catch (e: androidx.test.uiautomator.StaleObjectException) { null }
        var waits: List<androidx.test.uiautomator.UiObject2> = emptyList()
        var depth = 0
        while (container != null && depth < 4) {
            waits = try { container.findObjects(By.text("Wait")) } catch (e: androidx.test.uiautomator.StaleObjectException) { emptyList() }
            if (waits.isNotEmpty()) break
            container = try { container.parent } catch (e: androidx.test.uiautomator.StaleObjectException) { null }
            depth += 1
        }
        val waitPkg = try { waits.firstOrNull()?.applicationPackage } catch (e: androidx.test.uiautomator.StaleObjectException) { null }
        AnrDialogGate.verifyBeforeClick(first, again, nodes.size, waits.size, waitPkg)
        try { waits[0].click() } catch (e: androidx.test.uiautomator.StaleObjectException) {
            throw AssertionError("Wait button vanished before the click")
        }
    }

    private fun waitForAppWindow(device: UiDevice, budgetMs: Long): Boolean {
        val sel = By.pkg(ctx.packageName).depth(0)
        return AnrDialogGate.waitForWindow(
            budgetMs = budgetMs, pollMs = 1000, taps = taps, now = { System.currentTimeMillis() },
            dialog = { foundDialog(device) },
            capture = { n, d -> captureDialog(device, n, d) },
            tap = { d -> tapWaitInsideDialog(device, d) },
            windowUp = { ms -> device.wait(Until.hasObject(sel), ms) },
        )
    }

    /** Same instant query as the original second check; a tolerated dialog on screen is captured, tapped, then the query runs again. */
    private fun appWindowPersists(device: UiDevice): Boolean {
        val sel = By.pkg(ctx.packageName).depth(0)
        return AnrDialogGate.persists(
            taps = taps,
            dialog = { foundDialog(device) },
            capture = { n, d -> captureDialog(device, n, d) },
            tap = { d -> tapWaitInsideDialog(device, d) },
            hasWindow = { device.hasObject(sel) },
        )
    }

    private fun captureWindowFailure(device: UiDevice) {
        val tag = "ZephyrVerifyDiag"
        try {
            val dir = ctx.getExternalFilesDir(null)
            if (dir == null) { Log.e(tag, "no external files dir"); return }
            try { device.takeScreenshot(java.io.File(dir, "verify-fail.png")) } catch (e: Throwable) { Log.e(tag, "screenshot failed", e) }
            var out: java.io.OutputStream? = null
            try {
                out = java.io.FileOutputStream(java.io.File(dir, "verify-fail-hierarchy.xml"))
                device.dumpWindowHierarchy(out)
            } catch (e: Throwable) { Log.e(tag, "hierarchy dump failed", e) } finally {
                if (out != null) { try { out.close() } catch (e: Throwable) { Log.e(tag, "hierarchy close failed", e) } }
            }
            var w: java.io.Writer? = null
            try {
                val text = device.executeShellCommand("dumpsys window")
                if (text != null) {
                    w = java.io.OutputStreamWriter(java.io.FileOutputStream(java.io.File(dir, "verify-fail-window.txt")), "UTF-8")
                    w.write(text)
                }
            } catch (e: Throwable) { Log.e(tag, "window dump failed", e) } finally {
                if (w != null) { try { w.close() } catch (e: Throwable) { Log.e(tag, "window close failed", e) } }
            }
            try {
                // Is the query wrong or is nothing there? Same package without depth, and every root's package.
                Log.e(tag, "hasObject(pkg, no depth)=" + device.hasObject(By.pkg(ctx.packageName)))
                val roots = device.findObjects(By.depth(0))
                Log.e(tag, "depth-0 roots=" + roots.size)
                var i = 0
                while (i < roots.size) { Log.e(tag, "root " + i + " pkg=" + roots.get(i).applicationPackage); i = i + 1 }
            } catch (e: Throwable) { Log.e(tag, "query diagnostic failed", e) }
        } catch (e: Throwable) { Log.e(tag, "capture failed", e) }
    }
}
