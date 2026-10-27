package com.bingemote

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Teardown 1b: the lock-screen controls service stops after a few minutes of
 *  nothing playing - and never cuts a paused or playing session. */
class ControlsIdlePolicyTest {

    private val w = 5 * 60_000L

    @Test fun `paused and playing never stop the controls`() {
        val p = ControlsIdlePolicy()
        var t = 1_000_000L
        repeat(200) { assertFalse(p.onSuccess(t, "paused")); t += 2_000 }
        repeat(200) { assertFalse(p.onSuccess(t, "playing")); t += 2_000 }
    }

    @Test fun `a stopped player ends the controls after the window`() {
        val p = ControlsIdlePolicy()
        val t0 = 1_000_000L
        assertFalse(p.onSuccess(t0, "stopped"))
        assertFalse(p.onSuccess(t0 + w - 1, "stopped"))
        assertTrue(p.onSuccess(t0 + w, "stopped"))
    }

    @Test fun `playing resets the stopped stretch`() {
        val p = ControlsIdlePolicy()
        val t0 = 1_000_000L
        assertFalse(p.onSuccess(t0, "stopped"))
        assertFalse(p.onSuccess(t0 + w - 1, "stopped"))
        assertFalse(p.onSuccess(t0 + w, "playing")) // movie resumed
        assertFalse(p.onSuccess(t0 + w + w - 1, "stopped")) // streak restarted
        assertTrue(p.onSuccess(t0 + w + w + w, "stopped"))
    }

    @Test fun `the Zephyr Player's capitalized states end the controls`() {
        // S-1: the Player reports LibVLC enum names ("Stopped", "Ended") - they
        // must end the controls exactly like VLC's lowercase "stopped".
        val p = ControlsIdlePolicy()
        val t0 = 1_000_000L
        assertFalse(p.onSuccess(t0, "Stopped"))
        assertFalse(p.onSuccess(t0 + w - 1, "Stopped"))
        assertTrue(p.onSuccess(t0 + w, "Stopped"))

        val q = ControlsIdlePolicy()
        assertFalse(q.onSuccess(t0, "Ended"))
        assertFalse(q.onSuccess(t0 + w - 1, "Ended"))
        assertTrue(q.onSuccess(t0 + w, "Ended"))
    }

    @Test fun `an unanswered player ends the controls after the window`() {
        val p = ControlsIdlePolicy()
        val t0 = 1_000_000L
        assertFalse(p.onSuccess(t0, "playing"))
        assertFalse(p.onFailure(t0 + 10_000, 2))   // not inactive yet
        assertFalse(p.onFailure(t0 + w - 1, 5))    // inactive but inside the window
        assertTrue(p.onFailure(t0 + w, 6))         // window elapsed
    }

    @Test fun `a player that never answered stops after the window from boot`() {
        val p = ControlsIdlePolicy()
        assertFalse(p.onFailure(60_000, 5))        // 1 min after boot: keep trying
        assertTrue(p.onFailure(w + 60_000, 5))
    }
}
