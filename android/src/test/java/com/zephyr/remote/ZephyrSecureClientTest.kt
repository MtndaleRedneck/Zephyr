package com.zephyr.remote

import com.bingemote.TlsFixture
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** The Player client over real TLS loopback servers with throwaway keys: pin before bearer, only pairing enrolls, one-connection binding,
 *  no plaintext fallback, no redirect following. JVM and simulated: not Windows, not a device, not the real Player. */
class ZephyrSecureClientTest {
    private lateinit var fx: TlsFixture
    private val host = "127.0.0.1"

    @Before fun start() { fx = TlsFixture(); ZephyrClient.resetClientsForTests(); ZephyrClient.pinSource = fx.anyToken() }
    @After fun stop() { ZephyrClient.pinSource = null; ZephyrClient.resetClientsForTests(); fx.close() }

    @Test fun pairReturnsTheTokenAndTheKeyOfTheSameConnection() {
        fx.server.enqueue(MockResponse().setBody("{\"token\":\"tok-1\"}"))
        val p = ZephyrClient.pair(host, fx.port, "1234")
        assertEquals("tok-1", p.token); assertEquals(fx.pinHex, p.pinHex)
    }

    @Test fun bindingRefusesAKeyThatIsNotTheOneTheConnectionAccepted() {
        val spki = fx.spki; val h = com.bingemote.PinPolicy.hashSpki(spki)
        assertEquals(fx.pinHex, ZephyrClient.bound(spki, h, "t").pinHex)
        for ((leaf, obs) in listOf(spki to com.bingemote.PinPolicy.hashSpki(byteArrayOf(1, 2, 3)), null to h, spki to null)) {
            var refused = false
            try { ZephyrClient.bound(leaf, obs, "t") } catch (e: ZephyrClient.PairFailed) { refused = true }
            assertTrue(refused)
        }
    }

    @Test fun aPairConnectionDroppedAfterTheRequestSendsOnlyOneRequest_doesNotDiscriminateTheRetryFlag() {
        // NOTE: survives the retry-flag mutation (OkHttp does not replay this request class here), so it is not retry coverage. The first connection reads the whole request and is closed. With the retry flag on, OkHttp would send the request again
        // (the code would be presented twice and the second answer would be used): the server must see exactly one request.
        fx.server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.DISCONNECT_AFTER_REQUEST))
        fx.server.enqueue(MockResponse().setBody("{\"token\":\"tok-2\"}"))
        var threw = false
        try { ZephyrClient.pair(host, fx.port, "1234") } catch (e: java.io.IOException) { threw = true }
        assertTrue(threw)
        assertEquals("the code must not be presented a second time", 1, fx.server.requestCount)
    }

    @Test fun aPlainHttpPlayerFailsClosedForPairAndForEveryCall() {
        val plain = MockWebServer(); plain.start()
        try {
            plain.enqueue(MockResponse().setBody("{\"token\":\"tok\"}")); plain.enqueue(MockResponse().setBody("{}"))
            var a = false; var b = false
            try { ZephyrClient.pair(host, plain.port, "1234") } catch (e: java.io.IOException) { a = true }
            try { ZephyrClient.pause(fx.target("t", port = plain.port)) } catch (e: java.io.IOException) { b = true }
            assertTrue(a); assertTrue(b)
            // Whatever the plain server saw was TLS handshake bytes, never an HTTP request carrying the bearer or the code.
            repeat(plain.requestCount) {
                val r = plain.takeRequest(1, java.util.concurrent.TimeUnit.SECONDS)!!
                assertEquals(null, r.getHeader("Authorization")); assertFalse(r.body.readUtf8().contains("1234"))
            }
        } finally { plain.shutdown() }
    }

    @Test fun aChangedKeyIsRefusedAtTheHandshakeAndNoRequestOrBearerIsSent() {
        val other = TlsFixture()
        try {
            ZephyrClient.pinSource = ZephyrClient.PinSource { _ -> ZephyrClient.Pin.MATCH }   // the caller holds the OTHER PC's key (below); this server presents fx's
            fx.server.enqueue(MockResponse().setBody("{}"))
            var changed = false
            try { ZephyrClient.pause(fx.target("secret-bearer", pinHex = other.pinHex)) } catch (e: ZephyrClient.KeyChanged) { changed = true }
            assertTrue(changed); assertEquals(0, fx.server.requestCount)
        } finally { other.close() }
    }

    @Test fun sameTokenSamePcIdChangedKeyIsRefusedAtTheHandshakeAndTheIdleConnectionIsNotReused() {
        val other = TlsFixture()
        try {
            repeat(3) { fx.server.enqueue(MockResponse().setBody("{}")) }
            ZephyrClient.pause(fx.target("t")); ZephyrClient.pause(fx.target("t"))
            // Positive control: the second call really did reuse the first call's pooled connection (request sequence 0 then 1 on one connection).
            assertEquals(0, fx.server.takeRequest().sequenceNumber); assertEquals(1, fx.server.takeRequest().sequenceNumber)
            ZephyrClient.pinSource = ZephyrClient.PinSource { _ -> ZephyrClient.Pin.MATCH }   // the record itself now holds the other key
            // Same host, port, token and local id; only the key differs. Nothing may go over the idle connection or a new one.
            var changed = false
            try { ZephyrClient.pause(fx.target("t", pinHex = other.pinHex)) } catch (e: ZephyrClient.KeyChanged) { changed = true }
            assertTrue("the old client or its pooled connection must not be reused", changed)
            assertEquals(2, fx.server.requestCount)
            ZephyrClient.pinSource = fx.anyToken()
            // Going back to the original pairing works; the cache holds one client set, so it is rebuilt and opens a NEW connection (sequence 0), not the old idle one.
            ZephyrClient.pause(fx.target("t")); assertEquals(0, fx.server.takeRequest().sequenceNumber)
        } finally { other.close() }
    }

    @Test fun anOperationCapturedBeforeTheRecordChangedIsRefusedEvenWhenHostPortAndTokenAreUnchanged() {
        val other = TlsFixture()
        try {
            val t1 = fx.target("t")                                   // captured by the caller
            var stored: com.bingemote.Store.ZephyrTarget? = t1
            ZephyrClient.pinSource = fx.storedAs { stored }
            fx.server.enqueue(MockResponse().setBody("{}")); ZephyrClient.pause(t1)   // control: it sends while the record is unchanged
            assertEquals(1, fx.server.requestCount)
            for (changedRecord in listOf(fx.target("t", pinHex = other.pinHex), fx.target("t", pcId = com.bingemote.PairedCredential.newPcId()))) {
                stored = changedRecord                                // re-paired between capture and execution: same host, port, token
                var refused = false
                try { ZephyrClient.pause(t1) } catch (e: ZephyrClient.PairingChanged) { refused = true }
                assertTrue(refused)
            }
            assertEquals("nothing was sent for the stale tuple", 1, fx.server.requestCount)
        } finally { other.close() }
    }

    @Test fun aMalformedCapturedIdentityBuildsNoClientAndSendsNothing() {
        ZephyrClient.pinSource = ZephyrClient.PinSource { _ -> ZephyrClient.Pin.MATCH }
        for ((id, pinHex) in listOf("x" to fx.pinHex, fx.pcId to "zz", fx.pcId to fx.pinHex.take(60), "" to "")) {
            var refused = false
            try { ZephyrClient.pause(fx.target("t", pcId = id, pinHex = pinHex)) } catch (e: ZephyrClient.PinUnavailable) { refused = true }
            assertTrue(refused)
        }
        assertEquals(0, fx.server.requestCount)
    }

    @Test fun anOrdinaryCallNeverEnrollsAKeyItWasNotPairedWith() {
        // Pinned to nothing matching: the first-use rule applies only to pair().
        ZephyrClient.pinSource = ZephyrClient.PinSource { _ -> ZephyrClient.Pin.MATCH }
        var changed = false
        try { ZephyrClient.pause(fx.target("t", pinHex = "00".repeat(32))) } catch (e: ZephyrClient.KeyChanged) { changed = true }
        assertTrue(changed); assertEquals(0, fx.server.requestCount)
    }

    @Test fun aRedirectIsNotFollowed() {
        fx.server.enqueue(MockResponse().setResponseCode(302).addHeader("Location", "https://$host:${fx.port}/elsewhere"))
        fx.server.enqueue(MockResponse().setBody("{}"))
        var refused = false
        try { ZephyrClient.pause(fx.target("t")) } catch (e: java.io.IOException) { refused = true }
        assertTrue(refused); assertEquals(1, fx.server.requestCount)
    }

    @Test fun controlCallsKeepTheShortReadTimeout() {
        fx.server.enqueue(MockResponse().setBody("{}").setHeadersDelay(7, java.util.concurrent.TimeUnit.SECONDS))
        var timedOut = false
        try { ZephyrClient.pause(fx.target("t")) } catch (e: java.io.IOException) { timedOut = true }
        assertTrue(timedOut)
    }

    @Test fun everyPlayerRouteIsBuiltFromAFixedHttpsSchemeNotFromAnythingStored() {
        val f = java.io.File("src/main/java/com/zephyr/remote/ZephyrClient.kt").takeIf { it.exists() }
            ?: java.io.File("android/src/main/java/com/zephyr/remote/ZephyrClient.kt")
        val src = f.readText()
        assertTrue(src.contains("private const val SCHEME = \"https\""))
        assertFalse("no cleartext URL literal", Regex("\"http://").containsMatchIn(src))
        assertEquals("every request URL comes from the one fixed-scheme builder", Regex("\\.url\\(").findAll(src).count(), Regex("\\.url\\(url\\(").findAll(src).count())
    }
}
