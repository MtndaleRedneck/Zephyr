package com.bingemote
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
class ReleaseIdentityTest {
 @Test fun clearVisualPairingSynchronously() {
  val ctx = InstrumentationRegistry.getInstrumentation().targetContext
  ctx.getSharedPreferences("settings",0).edit().remove("zephyrToken").commit()
  val secrets=androidx.security.crypto.EncryptedSharedPreferences.create(ctx,"secrets",
   androidx.security.crypto.MasterKey.Builder(ctx).setKeyScheme(androidx.security.crypto.MasterKey.KeyScheme.AES256_GCM).build(),
   androidx.security.crypto.EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
   androidx.security.crypto.EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM)
  assertTrue(secrets.edit().remove("zephyrToken").commit())
  assertEquals("", Store(ctx).zephyrToken)
 }

 @Test fun separateIdentityAndSavedChoices() {
  val ctx = InstrumentationRegistry.getInstrumentation().targetContext
  assertTrue(ctx.packageName.startsWith("com.zephyr.remote"))
  val p = ctx.getSharedPreferences("settings", 0)
  p.edit().remove("skipMainSec").remove("skipSecondarySec").commit()
  assertEquals(10, Store(ctx).skipMainSec); assertEquals(30, Store(ctx).skipSecondarySec)
  p.edit().putInt("skipMainSec",5).putInt("skipSecondarySec",180).commit()
  assertEquals(5, Store(ctx).skipMainSec); assertEquals(180, Store(ctx).skipSecondarySec)
  assertEquals(listOf(5,10,15,30), MAIN_SKIP_OPTIONS)
  assertEquals(listOf(15,30,60,120,180), SECONDARY_SKIP_OPTIONS)
  p.edit().remove("skipMainSec").remove("skipSecondarySec").commit()
 }
}
