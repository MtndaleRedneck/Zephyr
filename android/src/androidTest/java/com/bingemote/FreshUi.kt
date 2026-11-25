package com.bingemote

import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.platform.app.InstrumentationRegistry

/** Text lookups that read the active window straight from UiAutomation instead of through UiDevice's selector
 *  path. CI evidence (run 45503375454) showed UiDevice's tree still listing a node that the screen and the raw
 *  active-window root no longer had. Same assertions as before, one consistent read path. A null root is treated
 *  as "unknown" and retried, never as "absent". */
object FreshUi {
    private fun root(): AccessibilityNodeInfo? {
        val ua = InstrumentationRegistry.getInstrumentation().uiAutomation
        repeat(10) { ua.rootInActiveWindow?.let { return it }; Thread.sleep(100) }
        return null
    }
    private fun walk(n: AccessibilityNodeInfo?, text: String): Boolean {
        if (n == null) return false
        if (n.text?.toString() == text || n.contentDescription?.toString() == text) return true
        for (i in 0 until n.childCount) if (walk(n.getChild(i), text)) return true
        return false
    }
    /** True only if a readable root exists and contains [text]. */
    fun has(text: String): Boolean = walk(root(), text)
    /** Waits until a readable root exists and does not contain [text]. */
    fun waitGone(text: String, timeoutMs: Long): Boolean {
        val end = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < end) {
            val r = root()
            if (r != null && !walk(r, text)) return true
            Thread.sleep(250)
        }
        return false
    }
}
