package com.bingemote

import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class AutomaticOnlySetupTest {
    private companion object {
        // Selector anchors, copied verbatim from SetupScreen.kt (title, pair-code
        // placeholder, pair-card body, demo helper row "MY-PC").
        // One definition shared by the control and the primary below: the two tests
        // can never drift onto different selectors for the same element.
        const val SETUP_TITLE = "Find your PC"
        const val PAIR_CODE_LABEL = "Code from your PC"
        const val PAIR_CARD_BODY = "Enter the code Zephyr VLC Remote is showing on your PC"
        const val DEMO_PC_ROW = "MY-PC  ("
    }
    // Failure evidence: registered like the sibling UI test classes. failed()
    // writes bm-dump-<method>.{png,uix,txt} + the paired bm-dump2-* recurrence
    // capture to getExternalFilesDir on ANY failure, including the hide asserts
    // below, which have no capture of their own (the *-fail files cover only the
    // reveal asserts). scripts/instrumented.sh pulls these names on a failed suite.
    @get:Rule val failureDump = FailureDumpRule()
    // Interval bound: FailureDumpRule.starting() runs before this @Before and fails the test on any ANR dialog that is not the
    // exact Pixel Launcher identity. This Wait click only runs after the rule passed, so an ANR dialog that appears in the gap
    // between the rule and this line, or any other node labelled "Wait", can still be tapped. Bounded, not proven away.
    @org.junit.Before fun dismissSystemDialogs() {
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        val wait = device.findObject(By.text("Wait"))
        wait?.click()
        device.waitForIdle()
    }

    @Test fun updateSuccessSharesVersionRowAndExpires() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        ctx.startActivity(Intent(ctx, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", "settings").putExtra("updateResult", "You're up to date."))
        // Rig intentionally starts at bottom, so the Settings header is offscreen.
        assertTrue(device.wait(Until.hasObject(By.text("Version " + BuildConfig.VERSION_NAME)), 4000))
        val version = device.findObject(By.text("Version " + BuildConfig.VERSION_NAME))
        val success = device.findObject(By.text("You're up to date."))
        assertFalse(device.hasObject(By.text("Updates")))
        assertNotNull(version)
        assertNotNull(success)
        assertEquals(version.visibleBounds.centerY(), success.visibleBounds.centerY())
        assertTrue(success.visibleBounds.left > version.visibleBounds.right)
        device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-updates-success.png"))
        assertTrue(device.wait(Until.gone(By.text("You're up to date.")), 7000))
        assertTrue(device.hasObject(By.text("Version " + BuildConfig.VERSION_NAME)))
        device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-updates-expired.png"))
    }

    @Test fun cancelReturnsToSettingsAndBottomBackIsAbsent() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        Store(ctx).setupDone = true
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        ctx.startActivity(Intent(ctx, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", "setup").putExtra("pairdemo", "true"))
        assertTrue(device.wait(Until.hasObject(By.text("Cancel")), 10000))
        assertFalse(device.hasObject(By.text("Back")))
        device.findObject(By.text("Cancel")).click()
        assertTrue(device.wait(Until.hasObject(By.text("Settings")), 5000))
        device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-cancel-settings.png"))
    }

    /**
     * Positive presence/selector control for the hide-transition poll in
     * pairEntryNeedsSelectedPcOrConfirmedAddress. Same screen, same reveal-with-scroll
     * loop, same By.text target, and the SAME Until.gone poll mechanism as the primary
     * assert: absent before selection, present with the pair card after it, and the
     * gone poll must not report gone while the selection stands. If the selector or
     * the gone poll were vacuous, the primary test's assertFalse could pass without
     * proving the hide, and this control fails.
     */
    @Test fun pairCodeEntryPresenceTracksSelection() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        ctx.startActivity(Intent(ctx, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", "setup").putExtra("pairdemo", "true"))
        assertTrue(device.wait(Until.hasObject(By.text(SETUP_TITLE)), 10000))
        assertFalse("pair-code entry visible before any selection",
            device.hasObject(By.text(PAIR_CODE_LABEL)))
        val row = device.findObject(By.textContains(DEMO_PC_ROW))
        assertNotNull("demo PC row not found", row)
        row.click()
        // Identical reveal-with-scroll to the primary test: the card can render below the
        // fold, so a presence assert without the scroll fails on layout, not on behavior.
        val revealBy = System.currentTimeMillis() + 10000
        while (!device.hasObject(By.text(PAIR_CODE_LABEL)) && System.currentTimeMillis() < revealBy) {
            device.wait(Until.hasObject(By.text(PAIR_CODE_LABEL)), 700)
            if (!device.hasObject(By.text(PAIR_CODE_LABEL))) {
                device.swipe(device.displayWidth / 2, device.displayHeight * 4 / 5,
                    device.displayWidth / 2, device.displayHeight / 2, 20)
            }
        }
        assertTrue("pair-code entry never appeared after selection", device.hasObject(By.text(PAIR_CODE_LABEL)))
        // Selected-state proof: the pair card renders only while chosen != null.
        assertTrue("pair card body missing - selection state not established",
            device.hasObject(By.textContains(PAIR_CARD_BODY)))
        // Non-vacuity of the gone poll itself: while the selection stands, Until.gone on the
        // same selector must keep timing out (return false) for a bounded window.
        assertFalse("gone poll reported gone while the selection still stood",
            device.wait(Until.gone(By.text(PAIR_CODE_LABEL)), 2000))
        assertTrue("pair-code entry vanished while the selection still stood",
            device.hasObject(By.text(PAIR_CODE_LABEL)))
    }

    @Test fun pairEntryNeedsSelectedPcOrConfirmedAddress() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        ctx.startActivity(Intent(ctx, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", "setup").putExtra("pairdemo", "true"))
        assertTrue(device.wait(Until.hasObject(By.text(SETUP_TITLE)), 10000))
        assertFalse(device.hasObject(By.text(PAIR_CODE_LABEL)))
        device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-before-selection.png"))
        val pcRow = device.findObject(By.textContains(DEMO_PC_ROW))
        assertNotNull("demo PC row not found", pcRow)
        pcRow.click()
        // Condition-driven reveal (review-driven): the pair-code section can render
        // without any scroll on large screens and can need a scroll on others, so
        // alternate a bounded render wait with a conditional scroll instead of fixed
        // swipes/sleeps. Run 45435505970 failed this assert with all four captures
        // absent (adb "No such file" stubs in the artifact), so on failure also
        // capture the actual screen state and hierarchy as evidence.
        val revealBy = System.currentTimeMillis() + 10000
        while (!device.hasObject(By.text(PAIR_CODE_LABEL)) && System.currentTimeMillis() < revealBy) {
            device.wait(Until.hasObject(By.text(PAIR_CODE_LABEL)), 700)
            if (!device.hasObject(By.text(PAIR_CODE_LABEL))) {
                device.swipe(device.displayWidth / 2, device.displayHeight * 4 / 5,
                    device.displayWidth / 2, device.displayHeight / 2, 20)
            }
        }
        if (!device.hasObject(By.text(PAIR_CODE_LABEL))) {
            device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-selected-pc-fail.png"))
            device.dumpWindowHierarchy(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-selected-pc-fail.xml"))
        }
        assertTrue(device.hasObject(By.text(PAIR_CODE_LABEL)))
        device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-selected-pc.png"))
        device.swipe(device.displayWidth / 2, device.displayHeight / 3,
            device.displayWidth / 2, device.displayHeight * 4 / 5, 25)
        Thread.sleep(400)
        repeat(4) {
            if (!device.hasObject(By.text("Or type the PC's address:"))) {
                device.swipe(device.displayWidth / 2, device.displayHeight / 3,
                    device.displayWidth / 2, device.displayHeight * 4 / 5, 20)
                Thread.sleep(200)
            }
        }
        assertTrue(device.hasObject(By.text("Or type the PC's address:")))
        // Pre-condition pin (review-driven): the gone poll after setText is only
        // meaningful if the pair-code entry was present, with the selection still
        // standing, immediately before the address clears it - otherwise the poll
        // proves nothing. The pair card renders below the address section, so reveal
        // it again with the same condition loop, pin both facts, then return to the
        // field. Scrolling cannot clear the selection; only the setText below can
        // (SetupScreen.kt address onValueChange clears chosen).
        val pinBy = System.currentTimeMillis() + 10000
        while (!device.hasObject(By.text(PAIR_CODE_LABEL)) && System.currentTimeMillis() < pinBy) {
            device.wait(Until.hasObject(By.text(PAIR_CODE_LABEL)), 700)
            if (!device.hasObject(By.text(PAIR_CODE_LABEL))) {
                device.swipe(device.displayWidth / 2, device.displayHeight * 4 / 5,
                    device.displayWidth / 2, device.displayHeight / 2, 20)
            }
        }
        assertTrue("pair-code entry not present immediately before the address clears the selection",
            device.hasObject(By.text(PAIR_CODE_LABEL)))
        assertTrue("pair card body missing - selection not standing before the clear",
            device.hasObject(By.textContains(PAIR_CARD_BODY)))
        // Same-viewport precondition (test-proof strategy, no scrolling proof needed): the address
        // label, the "Use this address" button and the pair-code label must be on screen in ONE
        // snapshot before typing, with the card label fully inside the window. The pair card renders
        // below the field (SetupScreen: field, button, optional Selected row (needs a typed address),
        // then the Pair card), so one viewport that shows both ends of that span is observed
        // evidence of where the card sits. Nudge the page with small swipes toward that
        // viewport; if it cannot be established, fail closed (nothing is claimed).
        val fieldLabel = "Or type the PC's address:"
        fun sameViewport() = device.hasObject(By.text(fieldLabel)) &&
            device.hasObject(By.text("Use this address")) && device.hasObject(By.text(PAIR_CODE_LABEL))
        var nudges = 0
        while (!sameViewport() && nudges < 8) {
            if (!device.hasObject(By.text(fieldLabel)))
                device.swipe(device.displayWidth / 2, device.displayHeight * 45 / 100,
                    device.displayWidth / 2, device.displayHeight * 70 / 100, 20)
            else
                device.swipe(device.displayWidth / 2, device.displayHeight * 70 / 100,
                    device.displayWidth / 2, device.displayHeight * 55 / 100, 20)
            device.waitForIdle()
            nudges++
        }
        if (!sameViewport()) Triage.tree("same-viewport precondition not established before typing")
        assertTrue("address field, Use-this-address button and pair-code label are not on screen together - " +
            "the same-viewport proof cannot be made (fail closed)", sameViewport())
        fun bounds(text: String) = device.findObject(By.text(text))!!.visibleBounds
        val appBounds = device.findObject(By.pkg(ctx.packageName))!!.visibleBounds
        val fieldBefore = bounds(fieldLabel)
        val buttonBefore = bounds("Use this address")
        val cardLabelBefore = bounds(PAIR_CODE_LABEL)
        // Counterexample the clearance must cover (review): if the bug under test is present, typing
        // does not clear the selection, and SetupScreen (L284-286) inserts a "Selected: host:port" row
        // between the button and the card, which moves the card DOWN relative to the button. That row is
        // a Spacer(8.dp) plus a Text(fontSize = 13.sp) with the same default text style as the address
        // label, so its height is the address label's own measured height (this device's font scale and
        // line height included) plus 8dp at this device's density. Require the card label to stay inside
        // the window even after that insertion, so a still-present card could not be pushed out of the
        // tree and read as "gone". The row text is one short line ("Selected: 192.0.2.20:8488").
        val insertion = fieldBefore.height() +
            Math.round(8 * ctx.resources.displayMetrics.density)
        assertTrue("pair-code label not fully inside the app window before typing, with room for the Selected row " +
            "(insertion=$insertion px): the same-viewport proof cannot be made (fail closed)",
            cardLabelBefore.top >= appBounds.top && cardLabelBefore.height() > 0 &&
                cardLabelBefore.bottom + insertion <= appBounds.bottom)
        val fields = device.findObjects(By.clazz("android.widget.EditText"))
        val addressField = fields.minBy { it.visibleBounds.top }
        addressField.text = "192.0.2.20"
        // ACTION_SET_TEXT applies asynchronously through the IME/accessibility path, so a
        // read on the very next frame can race the state update (saw one CI run read back
        // empty while the identical tree passed in its sibling run). Poll briefly instead.
        val textDeadline = System.currentTimeMillis() + 3000
        while (addressField.text != "192.0.2.20" && System.currentTimeMillis() < textDeadline) Thread.sleep(100)
        assertEquals("192.0.2.20", addressField.text)
        // Same async timing class as the setText read-back above: typing the address
        // clears the selection, which removes the pair card, but the accessibility
        // SetText -> onValueChange -> recomposition chain is several asynchronous hops
        // and a one-shot gone-window can land inside them on a loaded emulator
        // (run 45424136265 on the old tree; BingeMote runs 45547260244 / 45549797139
        // hit the same class; Zephyr run 45543212735 failed this test once, unexplained).
        // The 10s figure is a worst-case CI wait
        // budget for the condition (matching this file's other transition budgets),
        // not a product-latency measurement; a healthy transition converges in the
        // first slices. The positive control (pairCodeEntryPresenceTracksSelection)
        // pins the same selector and gone poll from the presence side.
        // IME first: setText can raise the keyboard over the lower half of the screen
        // (where the pair card renders), distorting the tree the poll reads.
        device.waitForIdle()
        // Dismiss the keyboard only if it is shown (a blind back could leave the screen), and fail
        // closed if its state cannot be read: a keyboard over the card region would make an absence
        // read meaningless. (Window-list reads can lag a frame: one waitForIdle after the back.)
        fun imeShown(): Boolean {
            // Interactive window list (API 34 emulator, same source UiDevice reads its roots from): a
            // readable list always contains the app's own window, so an empty list means "not
            // readable" and fails closed instead of reading as "no keyboard".
            val windows = InstrumentationRegistry.getInstrumentation().uiAutomation.windows
            assertTrue("window list not readable - cannot rule out a keyboard over the card region",
                windows.isNotEmpty())
            return windows.any { it.type == android.view.accessibility.AccessibilityWindowInfo.TYPE_INPUT_METHOD }
        }
        if (imeShown()) { device.pressBack(); device.waitForIdle() }
        assertFalse("keyboard still shown - the card region may be covered", imeShown())
        // The window must be the one observed before typing (no resize from the keyboard).
        // Clearance was proven BEFORE typing (above), not from a post-hide shift: when the card goes, the
        // column shortens and Compose may clamp the scroll offset, so the anchors can move DOWN by up to
        // the card's height; a shifted-old-bounds check would then false-red, so none is made.
        assertEquals("app window bounds changed after typing", appBounds,
            device.findObject(By.pkg(ctx.packageName))!!.visibleBounds)
        val gone = device.wait(Until.gone(By.text(PAIR_CODE_LABEL)), 10000)
        if (!gone) Triage.tree("code field still shown after typing an address")
        assertTrue("pair-code entry still shown after the address cleared the selection", gone)
        assertEquals("app window bounds changed while the card disappeared", appBounds,
            device.findObject(By.pkg(ctx.packageName))!!.visibleBounds)
        assertTrue("address label no longer on screen - the observed viewport is not the one before typing",
            device.hasObject(By.text(fieldLabel)) && device.hasObject(By.text("Use this address")))
        val fieldAfter = bounds(fieldLabel)
        val buttonAfter = bounds("Use this address")
        val delta = fieldAfter.top - fieldBefore.top
        assertEquals("address label moved sideways", fieldBefore.left, fieldAfter.left)
        assertEquals("button did not move rigidly with the address label", buttonBefore.top + delta, buttonAfter.top)
        assertTrue("page moved UP while the card disappeared (delta=$delta): the old label region is not observed",
            delta >= 0)
        repeat(3) {
            if (!device.hasObject(By.text("Use this address"))) {
                device.swipe(device.displayWidth / 2, device.displayHeight * 4 / 5,
                    device.displayWidth / 2, device.displayHeight / 3, 20)
                Thread.sleep(250)
            }
        }
        assertTrue(device.wait(Until.hasObject(By.text("Use this address")), 3000))
        device.findObject(By.text("Use this address")).click()
        // Same condition-driven reveal as the selection case above: after the
        // address is confirmed the pair-code entry returns (with the Selected row),
        // and the fixed sleep+swipe pattern missed it in publish runs 45445415420 /
        // 45445418591 (line-126 assert, sibling jobs green on the same tree).
        val confirmRevealBy = System.currentTimeMillis() + 10000
        while (!device.hasObject(By.text(PAIR_CODE_LABEL)) && System.currentTimeMillis() < confirmRevealBy) {
            device.wait(Until.hasObject(By.text(PAIR_CODE_LABEL)), 700)
            if (!device.hasObject(By.text(PAIR_CODE_LABEL))) {
                device.swipe(device.displayWidth / 2, device.displayHeight * 4 / 5,
                    device.displayWidth / 2, device.displayHeight / 2, 20)
            }
        }
        if (!device.hasObject(By.text(PAIR_CODE_LABEL))) {
            device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-confirmed-address-fail.png"))
            device.dumpWindowHierarchy(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-confirmed-address-fail.xml"))
        }
        assertTrue(device.hasObject(By.text(PAIR_CODE_LABEL)))
        // The page was scrolled to reveal the code field; bring the Selected row back into view.
        repeat(3) {
            if (!device.hasObject(By.text("Selected: 192.0.2.20:${HelperClient.DEFAULT_PORT}"))) {
                device.swipe(device.displayWidth / 2, device.displayHeight / 3,
                    device.displayWidth / 2, device.displayHeight * 4 / 5, 20)
                Thread.sleep(250)
            }
        }
        if (!device.hasObject(By.text("Selected: 192.0.2.20:${HelperClient.DEFAULT_PORT}"))) Triage.tree("selected row missing after confirming address")
        assertTrue(device.hasObject(By.text("Selected: 192.0.2.20:${HelperClient.DEFAULT_PORT}")))
        device.takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "bm-setup-selected-address.png"))
    }

    @Test fun pendingAutomaticSetupHasDoneAndNoManualVlcFields() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        ctx.startActivity(Intent(ctx, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
            .putExtra("screen", "setup").putExtra("vlcpending", "true"))
        assertTrue(device.wait(Until.hasObject(By.text(SETUP_TITLE)), 10000))
        assertTrue(device.hasObject(By.text("Or type the PC's address:")))
        var foundDone = false
        repeat(7) {
            assertFalse(device.hasObject(By.text("VLC password")))
            assertFalse(device.hasObject(By.text("Turn on VLC's web remote")))
            assertFalse(device.hasObject(By.text("Set the password by hand instead")))
            assertFalse(device.hasObject(By.text("Hide the manual setup")))
            if (device.hasObject(By.text("Done"))) foundDone = true
            device.swipe(device.displayWidth / 2, device.displayHeight * 4 / 5,
                device.displayWidth / 2, device.displayHeight / 4, 25)
            Thread.sleep(300)
        }
        assertTrue("Automatic deferred setup still needs a Done action", foundDone)
    }
}
