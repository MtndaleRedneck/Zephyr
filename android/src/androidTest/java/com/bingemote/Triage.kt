package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.UiDevice

/** Evidence for triage: the visible screen content into logcat (tag ZephyrTriage) when a UI wait gives up.
 *  Only text, description and bounds of the app's own nodes, in lines short enough that logcat does not cut them. */
object Triage {
    fun tree(label: String) {
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
}
