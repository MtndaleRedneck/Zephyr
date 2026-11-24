package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Zephyr rewrite of an inherited BingeMote test. Pins the bar glyph normalizer's own contract
 * (NotificationArt.forBar), at a real device density: every glyph's drawn content fits the shared
 * 24dp x 26dp box and is centered inside its slot, horizontally and vertically.
 * It does NOT prove equal visible edge gaps: glyphs may be undersized uniformly or differently
 * (tall glyphs are capped by height), so visible gaps between neighbours can still differ.
 */
class BarSpacingTest {
    @Test fun barGlyphsShareOneCenteredContentBox() {
        val ctx = InstrumentationRegistry.getInstrumentation().targetContext
        val dm = ctx.resources.displayMetrics.density
        fun px(dp: Int) = (dp * dm + 0.5f).toInt()
        val out = px(36); val big = px(48)
        val icons = listOf(
            NotificationArt.forBar(NotificationArt.skip(big, false, "10"), out),
            NotificationArt.forBar(NotificationArt.volumeDown(big), out),
            NotificationArt.forBar(NotificationArt.pause(big), out),
            NotificationArt.forBar(NotificationArt.play(big), out),
            NotificationArt.forBar(NotificationArt.volumeUp(big), out),
            NotificationArt.forBar(NotificationArt.skip(big, true, "10"), out))
        val bounds = icons.map { NotificationArt.contentBounds(it) }
        assertTrue("every glyph drew something", bounds.all { it.width() > 0 && it.height() > 0 })
        // BingeMote 2e58cf1 capped tall glyphs at 26dp, so content widths are no longer equal and this
        // test no longer claims even edge gaps. What it pins: each glyph fits the shared 24dp x 26dp
        // box (undersizing is allowed) and is centered in its slot, both ways.
        val maxW = out * 24f / 36f + 1.5f
        val maxH = out * 26f / 36f + 1.5f
        bounds.forEachIndexed { i, r ->
            assertTrue("glyph $i width ${r.width()} fits the shared box", r.width() <= maxW)
            assertTrue("glyph $i height ${r.height()} fits the shared box", r.height() <= maxH)
            assertEquals("glyph $i horizontally centered", (out - r.width()) / 2.0, r.left.toDouble(), 1.5)
            assertEquals("glyph $i vertically centered", (out - r.height()) / 2.0, r.top.toDouble(), 1.5)
        }
    }
}
