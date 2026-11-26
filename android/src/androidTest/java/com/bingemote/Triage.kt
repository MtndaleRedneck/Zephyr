package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.UiDevice

/** Evidence for triage: the visible screen content into logcat (tag ZephyrTriage) when a UI wait gives up.
 *  Only text, description and bounds of the app's own nodes, in lines short enough that logcat does not cut them. */
object Triage {
    fun tree(label: String) {
        runCatching {
            val ctx = InstrumentationRegistry.getInstrumentation().targetContext
            UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
                .takeScreenshot(java.io.File(ctx.getExternalFilesDir(null), "triage-" + label.replace(Regex("[^A-Za-z0-9]+"), "-").take(40) + ".png"))
        }
        runCatching {
            val out = java.io.ByteArrayOutputStream()
            UiDevice.getInstance(InstrumentationRegistry.getInstrumentation()).dumpWindowHierarchy(out)
            val xml = out.toString()
            val rows = Regex("<node [^>]*>").findAll(xml).map { it.value }.filter { it.contains("package=\"${InstrumentationRegistry.getInstrumentation().targetContext.packageName}\"") }.mapNotNull { n ->
                val t = Regex("\\btext=\"([^\"]*)\"").find(n)?.groupValues?.get(1).orEmpty()
                val d = Regex("content-desc=\"([^\"]*)\"").find(n)?.groupValues?.get(1).orEmpty()
                val b = Regex("bounds=\"([^\"]*)\"").find(n)?.groupValues?.get(1).orEmpty()
                if (t.isEmpty() && d.isEmpty()) null else "[$t|$d|$b]"
            }.toList()
            android.util.Log.e("ZephyrTriage", "$label: ${rows.size} labelled nodes")
            rows.joinToString(" ").chunked(3000).forEachIndexed { i, c -> android.util.Log.e("ZephyrTriage", "$label #$i $c") }
        }
    }

    /** Diagnostic only (assertions are unchanged): after a wait gave up, re-reads the window through a
     *  freshly connected UiAutomation and logs how many labelled nodes (and whether [needle]) it sees,
     *  with timestamps, so a stale accessibility snapshot can be told apart from real screen content. */
    fun freshProbe(label: String, needle: String) {
        runCatching {
            val inst = InstrumentationRegistry.getInstrumentation()
            val before = System.currentTimeMillis()
            val old = inst.uiAutomation
            val oldCount = count(old.rootInActiveWindow, needle)
            val fresh = inst.getUiAutomation(android.app.UiAutomation.FLAG_DONT_SUPPRESS_ACCESSIBILITY_SERVICES)
            Thread.sleep(500)
            val newCount = count(fresh.rootInActiveWindow, needle)
            android.util.Log.e("ZephyrTriage", "$label probe t=$before: needle='$needle' seenByOldConnection=$oldCount seenAfterReconnect=$newCount sameConnection=${old === fresh}")
        }.onFailure { android.util.Log.e("ZephyrTriage", "$label probe failed: ${it.javaClass.simpleName}: ${it.message}") }
    }
    private fun count(root: android.view.accessibility.AccessibilityNodeInfo?, needle: String): Int {
        if (root == null) return -1
        var n = 0
        fun walk(x: android.view.accessibility.AccessibilityNodeInfo?) {
            if (x == null) return
            if (x.text?.toString() == needle || x.contentDescription?.toString() == needle) n++
            for (k in 0 until x.childCount) walk(x.getChild(k))
        }
        walk(root)
        return n
    }
}
