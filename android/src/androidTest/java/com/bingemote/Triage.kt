package com.bingemote

import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.UiDevice

/** Evidence for triage: the screen's accessibility tree into logcat (tag ZephyrTriage) when a UI wait gives up. */
object Triage {
    fun tree(label: String) {
        runCatching {
            val out = java.io.ByteArrayOutputStream()
            UiDevice.getInstance(InstrumentationRegistry.getInstrumentation()).dumpWindowHierarchy(out)
            android.util.Log.e("ZephyrTriage", label + ": " + out.toString().replace(Regex("\\s+"), " ").take(7000))
        }
    }
}
