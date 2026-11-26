package com.bingemote

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** CodeRabbit PR3 #10: fire("sub") used to build Cmd(generation = -1), which never equals
 *  subtitleRequest (starts 0, only increases), so Zephyr caption commands were always dropped. */
class SubtitleCmdKeepDropTest {
    @Test fun ungeneratedCommandIsNeverKept() {
        assertFalse(CmdPolicy.keepSubtitleCmd(Int.MIN_VALUE, 0))
        assertFalse(CmdPolicy.keepSubtitleCmd(Int.MIN_VALUE, 7))
    }
    @Test fun commandQueuedForCurrentGenerationIsKept() {
        assertTrue(CmdPolicy.keepSubtitleCmd(0, 0))
        assertTrue(CmdPolicy.keepSubtitleCmd(5, 5))
    }
    @Test fun commandQueuedBeforeSourceOrMovieChangeIsDropped() {
        assertFalse(CmdPolicy.keepSubtitleCmd(4, 5))
    }
    /** Source guard, not proof of delivery: no call site may enqueue a "sub" command through fire(); the
     *  Zephyr caption path must use fireSubtitle(generation). Whitespace tolerant. */
    @Test fun noSubtitleCallSiteUsesPlainFire() {
        val src = java.io.File("src/main/java/com/bingemote/RemoteState.kt").readText()
        assertFalse(Regex("""\bfire\(\s*"sub"""").containsMatchIn(src))
        assertTrue(Regex("""fireSubtitle\(\s*subtitleRequest\s*\)""").containsMatchIn(src))
    }
}
