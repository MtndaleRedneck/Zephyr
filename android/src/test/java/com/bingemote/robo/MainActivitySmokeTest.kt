package com.bingemote.robo

import android.os.Looper
import com.bingemote.*
import org.junit.Assert.assertNotNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/** SIMULATED Android framework (Robolectric), not a device. Runs only in the separate robolectricDebugUnitTest task. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class MainActivitySmokeTest {
    @Test fun mainActivityLaunchesAndDestroysWithoutCrashing() {
        val c = Robolectric.buildActivity(MainActivity::class.java).create().start().resume()
        shadowOf(Looper.getMainLooper()).idle()
        assertNotNull(c.get())
        c.pause().stop().destroy()
    }
}
