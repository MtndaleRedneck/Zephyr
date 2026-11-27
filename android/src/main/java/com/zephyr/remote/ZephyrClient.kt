package com.zephyr.remote

import java.util.concurrent.TimeUnit
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ConnectionPool
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import com.bingemote.PairedCredential
import com.bingemote.PinPolicy
import com.bingemote.PinMismatchException
import com.bingemote.PinningTrustManager
import com.bingemote.SecureHttp
import java.security.MessageDigest
import javax.net.ssl.SSLContext

/**
 * Talks to the Zephyr Player's PlaybackServer (protocol.md Handoff).
 * Pairing token rides in the Authorization header; the per-session stream token
 * stays in the URL path. Mirrors BingeMote's HelperClient patterns.
 */
object ZephyrClient {
    private val client = OkHttpClient.Builder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(5, TimeUnit.SECONDS)
        .build()

    private val playClient = client.newBuilder().readTimeout(40, TimeUnit.SECONDS).callTimeout(45, TimeUnit.SECONDS).build()

    /** "Find my PC" for the Zephyr Player: same broadcast question as the
     *  helper's discovery, answered with the player's own identity
     *  (ZEPHYR_PLAYER_HERE_V1) so the two apps never get confused. */
    const val REPLY_MAGIC = "ZEPHYR_PLAYER_HERE_V1"

    /** Zephyr Player 1.2+ answers discovery on its own port; the BingeMote helper
     *  keeps 8489 unconditionally, so the player never binds it. Players still on
     *  1.1.0 answer on 8489. "Find my PC" must hear BOTH generations. */
    const val DISCOVERY_PORT = 8490
    const val LEGACY_DISCOVERY_PORT = 8489
    fun discoverPlayers(waitMs: Int = 1500, targets: List<java.net.InetAddress>? = null): List<com.bingemote.DiscoveredHelper> {
        // Both generations are probed IN PARALLEL (S5): serial waits doubled
        // Find-my-PC to 3s and re-find to 6s for no reason.
        var legacy: List<com.bingemote.DiscoveredHelper> = emptyList()
        var current: List<com.bingemote.DiscoveredHelper> = emptyList()
        val t1 = kotlin.concurrent.thread { legacy = com.bingemote.HelperClient.discoverRaw(REPLY_MAGIC, waitMs, targets, LEGACY_DISCOVERY_PORT) }
        val t2 = kotlin.concurrent.thread { current = com.bingemote.HelperClient.discoverRaw(REPLY_MAGIC, waitMs, targets, DISCOVERY_PORT) }
        t1.join(); t2.join()
        val found = LinkedHashMap<String, com.bingemote.DiscoveredHelper>()
        // Legacy first, then current: if both generations somehow answer for one
        // host, the 1.2+ player wins.
        for (h in legacy) found[h.host] = h
        for (h in current) found[h.host] = h
        return found.values.toList()
    }

    /** POST /pair - trade the 6-digit code on the player screen for a pairing token.
     *  The one unauthenticated call; everything after rides on the returned token. */
    /** A pairing refusal with a plain, screen-ready reason (NEW-A1): the PC's
     *  own words when it sends them, one honest fallback per status when it does
     *  not. Distinct from a network failure, which is NOT a pairing answer. */
    class PairFailed(message: String) : java.io.IOException(message)

    /** Every Player route is HTTPS. The scheme is this constant and nothing stored or discovered ever chooses it. */
    private const val SCHEME = "https"
    private fun url(host: String, port: Int, path: String) = "$SCHEME://$host:$port$path"

    /** The PC's key is its identity: the OkHttp pin recipe of LiveWs (chain and host name are not the identity, the pinned key is). */
    private fun withPin(base: OkHttpClient, tm: PinningTrustManager): OkHttpClient {
        val ctx = SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), null) }
        return base.newBuilder()
            .sslSocketFactory(ctx.socketFactory, tm)
            .hostnameVerifier { _, _ -> true }
            .followRedirects(false).followSslRedirects(false)
            .build()
    }

    /** The key the PC presented on this response's own connection must be the key the trust manager accepted. Only then do token and pin leave together. */
    internal fun bound(leafSpki: ByteArray?, observed: ByteArray?, token: String): SecureHttp.BoundPin {
        if (leafSpki == null || observed == null || !MessageDigest.isEqual(PinPolicy.hashSpki(leafSpki), observed))
            throw PairFailed("Couldn't confirm which PC answered. Nothing was saved. Try again.")
        return SecureHttp.BoundPin(PairedCredential.toHex(observed), token)
    }

    /** POST /pair over one single-use connection: no retry, no pooled connection, a fresh trust manager. Pairing is the only call that
     *  may enroll a key, and the returned pin and token come from the same response on that connection. Nothing is stored here. */
    fun pair(host: String, port: Int, code: String): SecureHttp.BoundPin {
        val body = JSONObject().put("code", code).toString()
            .toRequestBody("application/json".toMediaType())
        val tm = PinningTrustManager(PinPolicy(), PairedCredential.newPcId(), true, markOnMismatch = false)
        // The key the PC presented on the very socket that carried this request (a network interceptor runs per connection, on that socket).
        val leafOnConnection = java.util.concurrent.atomic.AtomicReference<ByteArray?>()
        val oneUse = withPin(client.newBuilder()
            .retryOnConnectionFailure(false)
            .connectionPool(ConnectionPool(0, 1, TimeUnit.NANOSECONDS))
            .protocols(listOf(okhttp3.Protocol.HTTP_1_1))
            .addNetworkInterceptor { chain ->
                leafOnConnection.set((chain.connection()?.socket() as? javax.net.ssl.SSLSocket)?.session?.peerCertificates?.firstOrNull()?.publicKey?.encoded)
                chain.proceed(chain.request())
            }.build(), tm)
        val req = Request.Builder().url(url(host, port, "/pair")).post(body).build()
        oneUse.newCall(req).execute().use { resp ->
            val leaf = leafOnConnection.get()
            if (!resp.isSuccessful) {
                // The PC answers pairing refusals with {"error","detail"}: the
                // detail is written for him, so show it instead of a bare code.
                val raw = try { resp.body?.string().orEmpty() } catch (e: Exception) { "" }
                val detail = try { JSONObject(raw).optString("detail") } catch (e: Exception) { "" }
                val fallback = when (resp.code) {
                    403 -> "Wrong code - check the code on the player screen."
                    410 -> "That code expired - use the code showing on the PC screen now."
                    429 -> "Too many wrong codes. On the PC, press Pair for a new code, then enter it here."
                    409 -> "This PC already has too many phones paired - remove one first."
                    else -> "Player returned ${resp.code}"
                }
                throw PairFailed(if (detail.isNotBlank()) detail else fallback)
            }
            val token = JSONObject(resp.body!!.string()).getString("token")
            return bound(leaf, tm.observedHash, token)
        }
    }

    /** The stored PC identity (local id and key hash) that belongs to the matched pairing. */
    class Identity(val pcId: String, val pinHex: String)
    /** MATCH = the stored pairing is exactly the tuple the caller captured. CHANGED = a different pairing (or none) is stored.
     *  UNAVAILABLE = no verdict right now (the record is loading or being written); that is not evidence about the PC or the pairing. */
    sealed class Pin {
        object MATCH : Pin()
        object CHANGED : Pin()
        object UNAVAILABLE : Pin()
    }
    fun interface PinSource { fun check(t: com.bingemote.Store.ZephyrTarget): Pin }
    /** The stored pairing could not be checked at this moment: nothing is sent, and this is neither an outage nor a pairing change. */
    class PinUnavailable : java.io.IOException("Can't confirm the paired PC right now. Try again.")
    /** Unset means nothing is sent (fail closed); the app installs it once, and tests install their own explicitly. */
    @Volatile var pinSource: PinSource? = null
    /** The stored pairing changed since the caller captured its target: nothing is sent. */
    class PairingChanged : java.io.IOException("The paired PC changed while this was in progress.")
    /** The PC presented a key other than the remembered one: nothing was sent past the handshake. The only way forward is to pair again. */
    class KeyChanged : java.io.IOException("This PC's security key changed, so Zephyr Player was not contacted. If you did not expect this, check your network; to continue, pair again.")
    /** Whole-tuple check, made before any request: the stored pairing must be exactly the tuple the caller captured (host, port, token, id AND key).
     *  The client is built from the caller's own id and key, never from a replacement looked up now. */
    private fun pinned(t: com.bingemote.Store.ZephyrTarget): Identity {
        val src = pinSource ?: throw PinUnavailable()
        return when (src.check(t)) {
            // A malformed identity is never used to build a client: it is the same as no verdict.
            Pin.MATCH -> Identity(t.pcId, t.pinHex).takeIf { HEX32.matches(it.pcId) && HEX64.matches(it.pinHex) } ?: throw PinUnavailable()
            Pin.CHANGED -> throw PairingChanged()
            Pin.UNAVAILABLE -> throw PinUnavailable()
        }
    }

    private val HEX32 = Regex("[0-9a-f]{32}")
    private val HEX64 = Regex("[0-9a-f]{64}")
    private enum class Kind { NORMAL, PLAY, HEARTBEAT }
    private class Cached(val key: String, val clients: Map<Kind, OkHttpClient>)
    @Volatile private var cached: Cached? = null
    /** One set of clients per stored identity. A different pcId or key builds a new set and drops the old one, so a changed pairing never reuses a client holding an old pin. */
    private fun secureClient(kind: Kind, id: Identity): OkHttpClient {
        val key = id.pcId + "|" + id.pinHex
        cached?.let { if (it.key == key) return it.clients.getValue(kind) }
        synchronized(this) {
            cached?.let { if (it.key == key) return it.clients.getValue(kind) }
            val tm = PinningTrustManager(PinPolicy(mapOf(id.pcId to PairedCredential.pinBytes(id.pinHex))), id.pcId, false)
            val set = mapOf(Kind.NORMAL to withPin(client, tm), Kind.PLAY to withPin(playClient, tm), Kind.HEARTBEAT to withPin(heartbeatClient, tm))
            cached = Cached(key, set)
            return set.getValue(kind)
        }
    }
    /** Test hook: drop the cached clients. */
    internal fun resetClientsForTests() { cached = null }

    private fun exec(kind: Kind, id: Identity, req: Request): okhttp3.Response = try {
        secureClient(kind, id).newCall(req).execute()
    } catch (e: javax.net.ssl.SSLException) {
        var c: Throwable? = e
        while (c != null) { if (c is PinMismatchException) throw KeyChanged(); c = c.cause }
        throw e
    }

    private class Authed(val id: Identity, val builder: Request.Builder)
    private fun authReq(t: com.bingemote.Store.ZephyrTarget, path: String): Authed {
        val id = pinned(t)
        return Authed(id, Request.Builder().url(url(t.host, t.port, path)).header("Authorization", "Bearer ${t.token}"))
    }

    /** The PC removed this phone's pairing: distinct from an outage so the app can say "pair again". */
    class BadToken : java.io.IOException("This phone isn't paired with Zephyr Player anymore. Pair again in Settings.")

    private fun post(t: com.bingemote.Store.ZephyrTarget, path: String, json: JSONObject? = null) {
        val a = authReq(t, path)
        val req = a.builder
            .post((json ?: JSONObject()).toString().toRequestBody("application/json".toMediaType()))
            .build()
        exec(Kind.NORMAL, a.id, req).use { resp ->
            if (resp.code == 401) throw BadToken()
            if (!resp.isSuccessful) throw java.io.IOException("Player returned ${resp.code}")
        }
    }

    private fun get(t: com.bingemote.Store.ZephyrTarget, path: String): JSONObject {
        val a = authReq(t, path)
        exec(Kind.NORMAL, a.id, a.builder.get().build()).use { resp ->
            if (resp.code == 401) throw BadToken()
            if (!resp.isSuccessful) throw java.io.IOException("Player returned ${resp.code}")
            return JSONObject(resp.body!!.string())
        }
    }

    // ---- Library (protocol.md Library) ----

    data class LibraryEntry(val name: String, val dir: Boolean)

    /** GET /library/list?path=rel -> one level of the assigned folder, folders first. */
    fun libraryList(t: com.bingemote.Store.ZephyrTarget, path: String = ""): Pair<Boolean, List<LibraryEntry>> {
        val json = get(t, "/library/list?path=" + java.net.URLEncoder.encode(path, "UTF-8"))
        val entries = json.getJSONArray("entries")
        val out = ArrayList<LibraryEntry>(entries.length())
        for (i in 0 until entries.length()) {
            val e = entries.getJSONObject(i)
            out.add(LibraryEntry(e.getString("name"), e.getBoolean("dir")))
        }
        return json.optBoolean("atRoot", true) to out
    }

    /** GET /library/browse?path=abs -> folder-picker listing (drives or subfolders). */
    fun libraryBrowse(t: com.bingemote.Store.ZephyrTarget, path: String = ""): List<LibraryEntry> {
        val json = get(t, "/library/browse?path=" + java.net.URLEncoder.encode(path, "UTF-8"))
        val entries = json.getJSONArray("entries")
        val out = ArrayList<LibraryEntry>(entries.length())
        for (i in 0 until entries.length()) {
            val e = entries.getJSONObject(i)
            out.add(LibraryEntry(e.getString("name"), e.getBoolean("dir")))
        }
        return out
    }

    fun libraryFolder(t: com.bingemote.Store.ZephyrTarget): String =
        get(t, "/library/folder").optString("libraryFolder", "")

    fun setLibraryFolder(t: com.bingemote.Store.ZephyrTarget, folder: String) {
        post(t, "/library/folder", JSONObject().put("folder", folder))
    }

    // ---- Control (protocol.md Control) ----

    fun pause(t: com.bingemote.Store.ZephyrTarget) = post(t, "/control/pause")
    fun resume(t: com.bingemote.Store.ZephyrTarget) = post(t, "/control/resume")
    fun seek(t: com.bingemote.Store.ZephyrTarget, positionSec: Long) =
        post(t, "/control/seek", JSONObject().put("position", positionSec))
    fun volume(t: com.bingemote.Store.ZephyrTarget, volume: Int) =
        post(t, "/control/volume", JSONObject().put("volume", volume))
    fun mute(t: com.bingemote.Store.ZephyrTarget, mute: Boolean) =
        post(t, "/control/mute", JSONObject().put("mute", mute))
    fun captions(t: com.bingemote.Store.ZephyrTarget, track: Int) =
        post(t, "/control/captions", JSONObject().put("track", track))
    fun fullscreen(t: com.bingemote.Store.ZephyrTarget, on: Boolean) =
        post(t, "/control/fullscreen", JSONObject().put("on", on))
    fun controlStatus(t: com.bingemote.Store.ZephyrTarget): JSONObject =
        get(t, "/control/status")

    // ---- Input (protocol.md Input receiver) ----

    fun inputMove(t: com.bingemote.Store.ZephyrTarget, dx: Double, dy: Double) =
        post(t, "/input/move", JSONObject().put("dx", dx).put("dy", dy))
    private val heldButtons = java.util.concurrent.ConcurrentHashMap<String, () -> Unit>()
    private val heartbeatClient = client.newBuilder().callTimeout(2, TimeUnit.SECONDS).build()
    private val leaseTimer = java.util.concurrent.Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "zephyr-input-lease").apply { isDaemon = true } }
    init { leaseTimer.scheduleWithFixedDelay({ heldButtons.values.toSet().forEach { runCatching { it() } } }, 2, 2, TimeUnit.SECONDS) }
    private val leaseGate = Any()
    @Volatile private var leaseActive = true
    fun startInputLeases() { synchronized(leaseGate) { leaseActive = true } }
    private val leaseEpoch = java.util.concurrent.atomic.AtomicLong()
    fun stopInputLeases() { synchronized(leaseGate) { leaseActive = false; leaseEpoch.incrementAndGet(); heldButtons.clear() } }
    fun inputButton(t: com.bingemote.Store.ZephyrTarget, button: String, down: Boolean) {
        val epoch = leaseEpoch.get()
        val tk = "${t.host}:${t.port}:${t.token}:${t.pcId}:${t.pinHex}"
        val key = "$tk:$button"
        if (!down) heldButtons.remove(key)
        post(t, "/input/button", JSONObject().put("button", button).put("down", down))
        synchronized(leaseGate) { if (down && leaseActive && epoch == leaseEpoch.get()) heldButtons[key] = {
            // A pairing that changed since the press stops heartbeating the old PC's button (its lease lapses on the PC); the release is refused the same way.
            try { val a = authReq(t, "/input/heartbeat"); exec(Kind.HEARTBEAT, a.id, a.builder.post("{}".toRequestBody("application/json".toMediaType())).build()).use { if (it.code == 401) heldButtons.keys.removeAll { k -> k.startsWith("$tk:") } } }
            catch (e: PairingChanged) { heldButtons.remove(key) }
            catch (e: PinUnavailable) { heldButtons.remove(key) }
        } }
    }
    fun inputScroll(t: com.bingemote.Store.ZephyrTarget, dy: Double) =
        post(t, "/input/scroll", JSONObject().put("dy", dy))
    fun inputZoom(t: com.bingemote.Store.ZephyrTarget, delta: Double) =
        post(t, "/input/zoom", JSONObject().put("delta", delta))
    fun inputKey(t: com.bingemote.Store.ZephyrTarget, key: String) =
        post(t, "/input/key", JSONObject().put("key", key))
    fun inputType(t: com.bingemote.Store.ZephyrTarget, text: String) =
        post(t, "/input/type", JSONObject().put("text", text))

    /** POST /play - ask the player to open the stream. */
    fun play(t: com.bingemote.Store.ZephyrTarget, url: String, subtitles: List<String>, position: Long? = null) {
        val body = JSONObject()
            .put("url", url)
            .put("subtitles", JSONArray(subtitles))
            .apply { if (position != null) put("position", position) }
            .toString()
            .toRequestBody("application/json".toMediaType())
        val a = authReq(t, "/play")
        val req = a.builder.post(body).build()
        exec(Kind.PLAY, a.id, req).use { resp ->
            if (resp.code == 401) throw BadToken()
            if (!resp.isSuccessful) throw java.io.IOException("Player returned ${resp.code}")
            // Dark PC screen: the player only woke the screen and did NOT start the movie.
            val woke = try { JSONObject(resp.body?.string().orEmpty()).optBoolean("woke", false) } catch (_: Exception) { false }
            if (woke) throw ScreenWoke()
        }
    }

    /** The PC screen was off: the tap only woke it and nothing started. */
    class ScreenWoke : java.io.IOException("The PC screen was off, so that tap only woke it. Tap the movie again.")

    /** /control/status's subtitle track list (PC 1.2+); empty from older players. */
    fun parseSpuTracks(json: JSONObject): List<com.bingemote.SubTrack> {
        val current = json.optInt("spu", -1)
        val arr = json.optJSONArray("spuTracks") ?: return emptyList()
        val out = ArrayList<com.bingemote.SubTrack>(arr.length())
        for (i in 0 until arr.length()) {
            val t = arr.optJSONObject(i) ?: continue
            // optInt + skip: one malformed track must not fail the whole poll and
            // count as an outage (S13).
            if (!t.has("id")) continue
            val id = t.optInt("id", Int.MIN_VALUE)
            if (id == Int.MIN_VALUE) continue
            out.add(com.bingemote.SubTrack(id, t.optString("name", "Track ${i + 1}"), id == current))
        }
        return out
    }
}
