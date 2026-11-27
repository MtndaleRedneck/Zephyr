package com.bingemote.robo

import android.content.Context
import android.content.SharedPreferences
import com.bingemote.*
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.lang.reflect.InvocationTargetException
import java.lang.reflect.Proxy
import kotlin.coroutines.CoroutineContext

/**
 * SIMULATED Android framework (Robolectric): not a real framework, Keystore or device. Runs only in the separate robolectricDebugUnitTest task.
 * Tests that say INJECTED use Robolectric-simulated, unencrypted SharedPreferences as the backing: not EncryptedSharedPreferences.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class RobolectricStoreRuntimeTest {
    private val ctx: Context get() = RuntimeEnvironment.getApplication()

    /** Hand-written dispatcher: work runs only when the test steps it. No sleeps, no test-dispatcher dependency. */
    private class Queued : CoroutineDispatcher() {
        private val q = ArrayDeque<Runnable>()
        override fun dispatch(context: CoroutineContext, block: Runnable) { synchronized(q) { q.addLast(block) } }
        fun runAll() { while (true) { val r = synchronized(q) { q.removeFirstOrNull() } ?: return; r.run() } }
    }

    private fun logged(real: SharedPreferences, log: MutableList<String>, name: String): SharedPreferences =
        Proxy.newProxyInstance(javaClass.classLoader, arrayOf(SharedPreferences::class.java)) { _, m, a ->
            if (m.name.startsWith("get") || m.name == "contains") log.add("$name.${m.name}(${a?.getOrNull(0)})")
            try { m.invoke(real, *(a ?: emptyArray())) } catch (e: InvocationTargetException) { throw e.targetException }
        } as SharedPreferences

    private class Rig(val plain: SharedPreferences, val secrets: SharedPreferences, val log: MutableList<String>, val store: Store)
    private fun rig(tag: String): Rig {
        PairingPublication.resetForTests()
        val log = java.util.Collections.synchronizedList(ArrayList<String>())
        val rawPlain = ctx.getSharedPreferences("inj-plain-$tag", 0); rawPlain.edit().clear().putInt("helperPort", 8488).putString("host", "pc.local").commit()
        val rawSecrets = ctx.getSharedPreferences("inj-secret-$tag", 0); rawSecrets.edit().clear().putString("helperToken", "legacy-tok").commit()
        val store = Store(logged(rawPlain, log, "plain"), logged(rawSecrets, log, "secret"))
        return Rig(rawPlain, rawSecrets, log, store)
    }

    /** Observes what the current candidate does when AndroidKeyStore cannot initialize (Robolectric has none). */
    @Test fun failedKeystoreInitTakesTheFailClosedPathNotABenignFallback() {
        PairingPublication.resetForTests()
        val store = Store(ctx)
        assertTrue("precondition: the encrypted store could not open here (expected to flip if Robolectric ever ships a Keystore)", store.secureStoreBroken)
        try { store.vlcPassword = "x"; fail("a secret write must throw while broken") } catch (e: SecureStoreUnavailable) { }
        assertEquals("", store.vlcPassword)
        assertEquals("", store.helperToken)
        assertNull(store.zephyrTarget())
        assertEquals("", store.helperVlcSnapshot().vlcPassword)
    }

    /** INJECTED backing. Deterministic: the preload runs on a queued dispatcher the test steps. Fails if `startup?.join()` is removed from the preload. */
    @Test fun preloadIsParkedOnTheStartupJobUntilItCompletes() {
        val r = rig("pre"); val q = Queued(); val startup = Job()
        val scope = CoroutineScope(SupervisorJob() + Queued())   // other init coroutines on this scope are never stepped: only the preload runs, on q
        RemoteState(ctx, r.store, scope, startup, q)
        r.log.clear()                                           // RemoteState's constructor reads on the constructing thread by design
        q.runAll()                                              // the preload runs to its first suspension: the join
        assertEquals("no storage read while the startup job is incomplete: ${r.log}", emptyList<String>(), r.log.toList())
        assertNull(r.store.pairingPeek())
        assertEquals(8488, r.plain.getInt("helperPort", 0))     // the migration has not run
        r.store.migrateLegacyHelperPort()                       // the startup work, done by the test
        startup.complete()
        q.runAll()                                              // the preload resumes after the join and publishes
        assertEquals(8492, r.store.pairingPeek()?.endpoint?.port)
        scope.coroutineContext[Job]?.cancel()
    }

    /**
     * INJECTED backing. pollOnce is launched as a child on a hand-written queued dispatcher that the test steps. First step: the poll runs to its
     * first suspension, the startup join (its next line, refreshPairing, would flip vlcPaired via the debug-only force flag). So vlcPaired false while
     * the startup job is open, after the poll was actually stepped, is a direct observation that it is parked. Second step after completion: it resumes.
     * Fails if the join is removed. No yield counting, no timing.
     */
    @Test fun pollOnceWaitsForTheStartupJobBeforeRefreshingPairing() {
        val r = rig("poll"); val q = Queued(); val startup = Job()
        val scope = CoroutineScope(SupervisorJob() + Queued())   // other init coroutines never stepped
        val remote = RemoteState(ctx, r.store, scope, startup, q)
        remote.debugForcePaired = true
        assertTrue("precondition: not yet refreshed", !remote.vlcPaired)
        var entered = false
        val poll = CoroutineScope(SupervisorJob() + q).launch { entered = true; remote.pollOnce() }
        q.runAll()                                              // step 1: the poll starts and parks on the startup join
        assertTrue("the poll child was actually scheduled and ran", entered)
        assertTrue("pollOnce is parked", poll.isActive)
        assertTrue("pollOnce must not refresh pairing while the startup job is open", !remote.vlcPaired)
        startup.complete()
        q.runAll()                                              // step 2: the parked poll resumes past the join
        assertTrue("after the startup job completes pollOnce proceeds", remote.vlcPaired)
        poll.cancel()
    }
}
