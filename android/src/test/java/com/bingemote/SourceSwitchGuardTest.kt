package com.bingemote

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** Structural guards (source-level, not behavioural): helper-only subtitle work is gated on
 *  the selected source and invalidated on a source switch, and the helper pair persists the
 *  token before the endpoint so a failed secure write leaves the prior pairing intact. */
class SourceSwitchGuardTest {
    private fun src(n: String) = File("src/main/java/com/bingemote/$n").readText()
    private fun body(s: String, sig: String): String {
        val i = s.indexOf(sig); assertTrue("missing $sig", i >= 0)
        return s.substring(i, minOf(s.length, i + 700))
    }

    @Test fun `helper subtitle entry points refuse when Player is selected`() {
        val s = src("RemoteState.kt")
        for (sig in listOf("fun refreshSubtitleFiles(", "fun loadSubtitleFile("))
            assertTrue(sig, body(s, sig).contains("store.selectedSource != Source.VLC) return"))
    }

    @Test fun `queued subtitle load rechecks the selected source`() {
        assertTrue(src("RemoteState.kt").contains("warningKey != subtitleWarningKey || store.selectedSource != Source.VLC"))
    }

    @Test fun `source switch invalidates subtitle counters and folder`() {
        val b = body(src("RemoteState.kt"), "fun selectSource(")
        for (t in listOf("subtitleRequest++", "subtitleListRequest++", "subtitleFolder = null", "subtitleListInFlight = false"))
            assertTrue(t, b.contains(t))
    }

    @Test fun `helper pair writes token before host and port and handles a broken store`() {
        val s = src("SetupScreen.kt")
        val t = s.indexOf("store.helperToken = paired.token")
        val h = s.indexOf("store.host = target.host", t)
        assertTrue("token must be written first", t in 0 until h)
        assertTrue(s.contains("catch (e: SecureStoreUnavailable)"))
    }
}
