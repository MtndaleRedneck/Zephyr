package com.bingemote

import org.junit.Assert.*
import org.junit.Test

class RecoveryFeedTest {
    private fun release(tag: String, version: String) = """{"tag_name":"$tag","draft":false,"prerelease":false,"body":"- BingeMote recovery","assets":[{"name":"Zephyr-Android-$version.apk","browser_download_url":"https://github.com/MtndaleRedneck/Zephyr-Dev/releases/download/$tag/Zephyr-Android-$version.apk","size":1000}]}"""

    @Test fun exact_shipped_parser_is_anchored_and_accepts_only_bridge() {
        val bridge = release("android-v1.12.0", "1.12.0")
        val separate = release("zephyr-android-v1.1.0", "1.1.0")
        assertEquals("1.12.0", ShippedAf2Updater.parseRelease(bridge, "1.0.0")?.versionName)
        assertNull(ShippedAf2Updater.parseRelease(separate, "1.0.0"))
        assertNull(ShippedAf2Updater.parseRelease(release("xandroid-v1.12.0", "1.12.0"), "1.0.0"))
        val outcome = ShippedAf2Updater.classifyFeed("[$separate,$bridge]", "1.0.0")
        assertTrue(outcome is ShippedAf2Updater.CheckOutcome.Newer)
        assertEquals("1.12.0", (outcome as ShippedAf2Updater.CheckOutcome.Newer).info.versionName)
    }

    @Test fun separate_updater_rejects_bridge_and_accepts_only_new_prefix() {
        val bridge = release("android-v1.12.0", "1.12.0")
        val separate = release("zephyr-android-v1.1.0", "1.1.0")
        assertNull(Updater.parseRelease(bridge, "1.0.0"))
        assertEquals("1.1.0", Updater.parseRelease(separate, "1.0.0")?.versionName)
        assertNull(Updater.parseRelease(release("xzephyr-android-v1.1.0", "1.1.0"), "1.0.0"))
        val outcome = Updater.classifyFeed("[$bridge,$separate]", "1.0.0")
        assertEquals("1.1.0", (outcome as Updater.CheckOutcome.Newer).info.versionName)
    }
}
