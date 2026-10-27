package com.bingemote

import org.json.JSONObject
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URL

/** A helper found on the network by UDP broadcast. */
data class DiscoveredHelper(val host: String, val port: Int, val name: String)

/**
 * Client for the Zephyr VLC Remote Windows helper: mouse, scroll, zoom and keyboard over LAN HTTP.
 * It only ever talks the Zephyr VLC Remote identity (port 8491 discovery / 8492 HTTP),
 * so a BingeMote install on the same PC is never discovered or driven by this app.
 * Every request carries the pairing token. Pairing trades a one-time 6-digit code
 * (shown on the PC) for the token. HTTP is not encrypted. Use only on a trusted private network;
 * a device on that network could observe the token and input.
 */
object HelperClient {
    const val DEFAULT_PORT = 8492 // Zephyr VLC Remote HTTP (BingeMote keeps 8488)
    const val DISCOVERY_PORT = 8491 // Zephyr VLC Remote discovery (BingeMote keeps 8489)
    private const val DISCOVER_MAGIC = "BINGEMOTE_DISCOVER_V1"
    internal const val REPLY_MAGIC = "ZEPHYR_VLC_REMOTE_HERE_V1"

    class Unreachable(val msg: String) : Exception(msg)
    /** Shown to the user. The raw socket text ("failed to connect to /192.168.1.5 (port 8488) after 2500ms") used to leak into Movies. */
    private const val NO_PC = "Can't reach Zephyr VLC Remote on the PC. Check that the PC is on and Zephyr VLC Remote is open."
    class BadToken(val msg: String) : Exception(msg)
    class BadPairCode(val msg: String) : Exception(msg)
    /** The PC could not be connected to at all (off, sleeping, new address). */
    class ConnectTimeout(val msg: String) : Exception(msg)

    /** The mouse screen's live connection: while it is open, move/scroll/zoom/button go
     * out as fire-and-forget frames on it and the HTTP request below never happens. */
    @Volatile var live: LiveWs? = null

    /** One shared worker for fire-and-forget calls (button release on background/leave) -
     * replaces ad-hoc `Thread {}` spawns that piled up one executor per gesture. */
    val io: java.util.concurrent.ExecutorService = java.util.concurrent.Executors.newSingleThreadExecutor { r ->
        Thread(r, "vlc-remote-io").apply { isDaemon = true }
    }

    private fun post(host: String, port: Int, path: String, body: JSONObject, token: String?, timeoutMs: Int = 5000) {
        val c = URL("http://$host:$port$path").openConnection() as HttpURLConnection
        try {
            c.connectTimeout = timeoutMs
            c.readTimeout = timeoutMs
            c.requestMethod = "POST"
            c.doOutput = true
            c.setRequestProperty("Content-Type", "application/json")
            if (token != null) c.setRequestProperty("X-Zephyr-VLC-Remote-Token", token)
            val bytes = body.toString().toByteArray(Charsets.UTF_8)
            c.outputStream.use { os: OutputStream -> os.write(bytes) }
            when (c.responseCode) {
                in 200..299 -> return
                else -> throw failure(c)
            }
        } catch (e: BadToken) { throw e }
        catch (e: Unreachable) { throw e }
        catch (e: ConnectTimeout) { throw e }
        catch (e: java.net.SocketTimeoutException) { throw ConnectTimeout(NO_PC) }
        catch (e: java.net.ConnectException) { throw ConnectTimeout(NO_PC) }
        catch (e: java.net.UnknownHostException) { throw ConnectTimeout(NO_PC) }
        catch (e: Exception) { throw Unreachable(NO_PC) }
        finally { releaseQuietly(c) }
    }

    /** True when the helper answers and accepts the token. */
    fun ping(host: String, port: Int, token: String): Boolean = try {
        // 1.5 s: a liveness probe must answer fast or be counted dead.
        post(host, port, "/ping", JSONObject(), token, 1500); true
    } catch (e: Exception) { false }

    private fun get(host: String, port: Int, path: String, token: String, timeoutMs: Int = 5000): JSONObject {
        val c = URL("http://$host:$port$path").openConnection() as HttpURLConnection
        try {
            c.connectTimeout = timeoutMs
            c.readTimeout = timeoutMs
            c.setRequestProperty("X-Zephyr-VLC-Remote-Token", token)
            when (c.responseCode) {
                in 200..299 -> return JSONObject(c.inputStream.use { it.readBytes().toString(Charsets.UTF_8) })
                else -> throw failure(c)
            }
        } catch (e: BadToken) { throw e }
        catch (e: Unreachable) { throw e }
        catch (e: ConnectTimeout) { throw e }
        catch (e: java.net.SocketTimeoutException) { throw ConnectTimeout(NO_PC) }
        catch (e: java.net.ConnectException) { throw ConnectTimeout(NO_PC) }
        catch (e: java.net.UnknownHostException) { throw ConnectTimeout(NO_PC) }
        catch (e: Exception) { throw Unreachable(NO_PC) }
        finally { releaseQuietly(c) }
    }

    /**
     * Turn an error response into the right exception. The helper answers its known
     * routes with a JSON body carrying a readable "detail" - show THAT, never a guess.
     * Only a bare 404 (no helper JSON) means the route is missing: the PC is too old.
     */
    private fun failure(c: HttpURLConnection): Exception {
        val code = c.responseCode
        // Read the error body ONCE (a second read always comes back empty, which used to
        // swallow the detail on 401/403).
        val text = try {
            c.errorStream?.use { it.readBytes().toString(Charsets.UTF_8) } ?: ""
        } catch (e: Exception) { "" }
        val json = try { JSONObject(text) } catch (e: Exception) { null }
        // The helper's IP guard also answers 403 ("not a private address") - that is a
        // network path problem, not a revoked pairing (I5). Only a token 401/403 with a
        // "bad token" body means pair again.
        if (code == 401 || code == 403) {
            val err = json?.optString("error")
            if (code == 401 || err == "bad token") return BadToken("The PC rejected the pairing token.")
        }
        val detail = json?.optString("detail")?.takeIf { it.isNotBlank() }
        if (detail != null) return Unreachable(detail)
        if (code == 404) return Unreachable("Zephyr VLC Remote on the PC is too old for this app.")
        return Unreachable("The PC answered with an error ($code).")
    }

    /** Let OkHttp pool the socket (keep-alive); disconnect() forced a fresh TCP handshake on every
     * mouse batch, which is what made the pointer lurch. Consuming the response lets the pool reuse it. */
    private fun releaseQuietly(c: HttpURLConnection) {
        try { c.inputStream.use { it.readBytes() } } catch (_: Exception) {
            try { c.errorStream?.use { it.readBytes() } } catch (_: Exception) {}
        }
    }

    /** Subfolders anywhere on the PC (drives when path is empty), so the folder picker
     * works with VLC closed. Folder names only - the PC never sends files. */
    fun browse(host: String, port: Int, token: String, path: String): List<String> {
        val enc = java.net.URLEncoder.encode(path, "UTF-8")
        val json = get(host, port, "/browse?path=$enc", token)
        val arr = json.getJSONArray("entries")
        val out = mutableListOf<String>()
        for (i in 0 until arr.length()) out.add(arr.getJSONObject(i).getString("name"))
        return out
    }

    /** The movies folder as set on the PC; null when the helper can't be asked (too old or down). */
    fun getMoviesFolder(host: String, port: Int, token: String): String? = try {
        get(host, port, "/config", token).optString("moviesFolder", "")
    } catch (e: Exception) { null }

    /** One level of the PC's movies folder, listed by the helper (works with VLC closed). */
    fun listMovies(host: String, port: Int, token: String, relPath: String): List<Pair<String, Boolean>> {
        val enc = java.net.URLEncoder.encode(relPath, "UTF-8")
        val json = get(host, port, "/movies/list?path=$enc", token)
        val out = mutableListOf<Pair<String, Boolean>>()
        val arr = json.getJSONArray("entries")
        for (i in 0 until arr.length()) {
            val e = arr.getJSONObject(i)
            out.add(e.getString("name") to e.getBoolean("dir"))
        }
        return out
    }

    /** Ask the helper to start VLC with this file (folder-relative path).
     * [resume] null = ask if a saved place exists; true = resume; false = start over.
     * Returns the raw response JSON; the caller checks "ask" (saved place found),
     * "ok" (playing), or "detail" (error text). */
    fun playMovie(host: String, port: Int, token: String, relPath: String, httpPort: Int, httpPassword: String, fullscreen: Boolean, resume: Boolean? = null): JSONObject {
        val body = JSONObject()
            .put("path", relPath).put("httpPort", httpPort).put("httpPassword", httpPassword).put("fullscreen", fullscreen)
        if (resume != null) body.put("resume", resume)
        return JSONObject(postForBody(host, port, "/movies/play", body, token))
    }

    /** post() that returns the response body (/movies/play answers carry the resume question). */
    private fun postForBody(host: String, port: Int, path: String, body: JSONObject, token: String?, timeoutMs: Int = 5000): String {
        val c = URL("http://$host:$port$path").openConnection() as HttpURLConnection
        try {
            c.connectTimeout = timeoutMs
            c.readTimeout = timeoutMs
            c.requestMethod = "POST"
            c.doOutput = true
            c.setRequestProperty("Content-Type", "application/json")
            if (token != null) c.setRequestProperty("X-Zephyr-VLC-Remote-Token", token)
            c.outputStream.use { os: OutputStream -> os.write(body.toString().toByteArray(Charsets.UTF_8)) }
            val text = (if (c.responseCode in 200..299) c.inputStream else c.errorStream)
                ?.use { s: InputStream -> s.readBytes().toString(Charsets.UTF_8) } ?: ""
            when (c.responseCode) {
                in 200..299 -> return text
                else -> throw failure(c)
            }
        } catch (e: BadToken) { throw e }
        catch (e: Unreachable) { throw e }
        catch (e: ConnectTimeout) { throw e }
        catch (e: java.net.SocketTimeoutException) { throw ConnectTimeout(NO_PC) }
        catch (e: java.net.ConnectException) { throw ConnectTimeout(NO_PC) }
        catch (e: java.net.UnknownHostException) { throw ConnectTimeout(NO_PC) }
        catch (e: Exception) { throw Unreachable(NO_PC) }
        finally { releaseQuietly(c) }
    }

    /** Hand the VLC web-remote password to the PC so it can write vlcrc (needs VLC closed).
     * Returns the skip reason ("vlc running") when the PC deferred it; null when written.
     * The PC retries the write by itself when VLC closes. */
    fun setupVlc(host: String, port: Int, token: String, password: String): String? {
        val resp = JSONObject(postForBody(host, port, "/vlc/setup", JSONObject().put("password", password), token))
        return if (resp.optBoolean("ok")) null else resp.optString("reason").ifBlank { null }
    }

    /** Save the movies folder on the PC (the helper owns it; both sides can set it). */
    fun setMoviesFolder(host: String, port: Int, token: String, folder: String) =
        post(host, port, "/config", JSONObject().put("moviesFolder", folder), token)

    /**
     * Trade a one-time 6-digit code (shown on the PC) for the long-lived pairing token.
     * Returns the token and the PC's name.
     */
    /** What a successful pairing returns. vlcPassword/vlcPending exist on helper 1.7+:
     *  the PC generates the VLC password and hands it over with the pair. An old helper
     *  leaves them null/false and the phone falls back to the manual password card. */
    data class PairResult(val token: String, val name: String, val vlcPassword: String?, val vlcPending: Boolean)

    internal fun parsePairJson(text: String): PairResult {
        val o = JSONObject(text)
        val pw = o.optString("vlcPassword", "").ifBlank { null }
        return PairResult(o.getString("token"), o.optString("name", "PC"), pw, o.optBoolean("vlcPending", false))
    }

    fun pair(host: String, port: Int, code: String): PairResult {
        val c = URL("http://$host:$port/pair").openConnection() as HttpURLConnection
        try {
            c.connectTimeout = 4000; c.readTimeout = 4000
            c.requestMethod = "POST"; c.doOutput = true
            c.setRequestProperty("Content-Type", "application/json")
            c.outputStream.use { it.write(JSONObject().put("code", code.trim()).toString().toByteArray(Charsets.UTF_8)) }
            val text = (if (c.responseCode in 200..299) c.inputStream else c.errorStream)?.use { s: InputStream ->
                s.readBytes().toString(Charsets.UTF_8)
            } ?: ""
            when (c.responseCode) {
                200 -> return parsePairJson(text)
                else -> {
                    // The helper answers refusals with {"error","detail"}: the detail is
                    // written for him, so show it instead of a bare code (NEW-A1 parity).
                    val detail = try { JSONObject(text).optString("detail") } catch (e: Exception) { "" }
                    val fallback = when (c.responseCode) {
                        401, 403 -> "Wrong code. Check the code on the PC and try again."
                        410 -> "That code expired - press Pair again in the Zephyr VLC Remote window on the PC."
                        429 -> "Too many tries. Press Pair again in the Zephyr VLC Remote window on the PC."
                        409 -> "That PC already has too many phones paired. Remove one in the Zephyr VLC Remote window first."
                        else -> "Pairing failed (error ${c.responseCode})."
                    }
                    throw BadPairCode(if (detail.isNotBlank()) detail else fallback)
                }
            }
        } catch (e: BadPairCode) { throw e }
        catch (e: Unreachable) { throw e }
        catch (e: Exception) { throw Unreachable(NO_PC) }
        finally { releaseQuietly(c) }
    }

    /** The PC's attestation for the automatic setup check: is the phone's VLC password
     * the live one in vlcrc (configured), or is a write waiting for VLC to close (pending)?
     * A green Done is grounded in this, never in a network error alone. */
    data class VlcSetupStatus(val configured: Boolean, val pending: Boolean)

    fun vlcSetupStatus(host: String, port: Int, token: String): VlcSetupStatus =
        get(host, port, "/vlc/setup-status", token).let {
            VlcSetupStatus(it.optBoolean("configured"), it.optBoolean("pending"))
        }

    /** The one-shot adoption fetch (automatic-first setup): the PC's VLC password,
     *  served exactly once per paired phone. null = the grant is spent or the helper is
     *  too old to know the endpoint. Throws Unreachable when the PC cannot be reached. */
    fun adoptVlcPassword(host: String, port: Int, token: String, requestId: String): String? {
        val o = postJson("http://$host:$port/vlc/adopt", token, JSONObject().put("requestId", requestId)) ?: return null
        return o.optString("password", "").ifBlank { null }
    }

    /** Confirm the password is stored: spends the one-shot grant for good. */
    fun ackVlcAdoption(host: String, port: Int, token: String, requestId: String): Boolean =
        postJson("http://$host:$port/vlc/adopt", token, JSONObject().put("requestId", requestId).put("ack", true)) != null

    /** POST a JSON body with the pairing token; null on any refusal (401/404/409/410) or error. */
    private fun postJson(url: String, token: String, body: JSONObject): JSONObject? {
        val c = URL(url).openConnection() as HttpURLConnection
        try {
            c.connectTimeout = 4000; c.readTimeout = 4000
            c.requestMethod = "POST"; c.doOutput = true
            c.setRequestProperty("Content-Type", "application/json")
            c.setRequestProperty("X-Zephyr-VLC-Remote-Token", token)
            c.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            if (c.responseCode !in 200..299) return null
            val text = c.inputStream.use { s: InputStream -> s.readBytes().toString(Charsets.UTF_8) }
            return JSONObject(text)
        } catch (e: Exception) { return null }
        finally { releaseQuietly(c) }
    }

    /** Find helpers on the network. Waits up to [waitMs] for replies. */
    fun discover(waitMs: Int = 1500): List<DiscoveredHelper> = discoverRaw(REPLY_MAGIC, waitMs)

    /** Same broadcast, any reply identity (Zephyr Player answers with its own
     *  magic). [targets] override exists for loopback tests only. */
    internal fun discoverRaw(replyMagic: String, waitMs: Int = 1500, targets: List<InetAddress>? = null, port: Int = DISCOVERY_PORT): List<DiscoveredHelper> {
        val found = LinkedHashMap<String, DiscoveredHelper>()
        var sock: DatagramSocket? = null
        try {
            sock = DatagramSocket()
            sock.broadcast = true
            sock.soTimeout = 250
            val payload = DISCOVER_MAGIC.toByteArray(Charsets.UTF_8)
            val targets = targets ?: broadcastTargets()
            val deadline = System.currentTimeMillis() + waitMs
            var sends = 0
            while (System.currentTimeMillis() < deadline) {
                // Six rounds to every broadcast address: routers drop lone broadcasts in both directions.
                if (sends < 6) {
                    for (t in targets) {
                        try { sock.send(DatagramPacket(payload, payload.size, t, port)) } catch (e: Exception) {}
                    }
                    sends++
                }
                try {
                    val buf = ByteArray(1024)
                    val reply = DatagramPacket(buf, buf.size)
                    sock.receive(reply)
                    val text = String(reply.data, 0, reply.length, Charsets.UTF_8)
                    val parts = text.split("|")
                    if (parts.size >= 3 && parts[0] == replyMagic) {
                        val port = parts[2].toIntOrNull() ?: DEFAULT_PORT
                        val host = reply.address.hostAddress ?: continue
                        found[host] = DiscoveredHelper(host, port, parts[1])
                    }
                } catch (e: java.net.SocketTimeoutException) { /* keep waiting */ }
            }
        } catch (e: Exception) { /* no network, no discovery */ }
        finally { sock?.close() }
        return found.values.toList()
    }

    /** 255.255.255.255 plus each interface's own subnet broadcast - some routers only pass one of them. */
    private fun broadcastTargets(): List<InetAddress> {
        val out = LinkedHashSet<InetAddress>()
        try { out.add(InetAddress.getByName("255.255.255.255")) } catch (e: Exception) {}
        try {
            val ifs = java.net.NetworkInterface.getNetworkInterfaces() ?: return out.toList()
            for (ni in ifs) {
                try { if (!ni.isUp || ni.isLoopback) continue } catch (e: Exception) { continue }
                for (ia in ni.interfaceAddresses) ia.broadcast?.let { out.add(it) }
            }
        } catch (e: Exception) {}
        return out.toList()
    }

    /** One ordered path: the live socket when it is open, plain HTTP when it is not -
     * never a per-frame mix of the two (mixed frames are ordered only per channel, and
     * interleaving them can reorder moves against clicks). A full socket queue drops the
     * frame instead of reordering it; the next move delta supersedes it. */
    private fun frame(host: String, port: Int, token: String, json: JSONObject, path: String, timeoutMs: Int = 5000) {
        val l = live
        if (l != null && l.open) {
            if (l.send(json)) Diag.wsSends.incrementAndGet() else Diag.wsDrops.incrementAndGet()
            return
        }
        Diag.httpFallbacks.incrementAndGet()
        post(host, port, path, json, token, timeoutMs)
    }

    fun move(host: String, port: Int, token: String, dx: Float, dy: Float) =
        frame(host, port, token, JSONObject().put("cmd", "move").put("dx", dx.toDouble()).put("dy", dy.toDouble()), "/mouse/move", 2500)

    /** button: "left" or "right". down=true presses, down=false releases. Frames are
     * ordered, so a click sent after moves on the live connection lands after them. */
    fun button(host: String, port: Int, token: String, button: String, down: Boolean) =
        frame(host, port, token, JSONObject().put("cmd", "button").put("button", button).put("down", down), "/mouse/button")

    fun scroll(host: String, port: Int, token: String, dy: Float) =
        frame(host, port, token, JSONObject().put("cmd", "scroll").put("dy", dy.toDouble()), "/mouse/scroll", 2500)

    /** Pinch zoom, sent to the PC as Ctrl + mouse wheel. delta > 0 zooms in. */
    fun zoom(host: String, port: Int, token: String, delta: Float) =
        frame(host, port, token, JSONObject().put("cmd", "zoom").put("delta", delta.toDouble()), "/mouse/zoom", 2500)

    /** Windows system volume, 0-100, and mute. Throws Unreachable when the helper does not answer. */
    fun volumeGet(host: String, port: Int, token: String): Pair<Int, Boolean> {
        val c = URL("http://$host:$port/volume/get").openConnection() as HttpURLConnection
        try {
            c.connectTimeout = 1500; c.readTimeout = 5000
            c.requestMethod = "POST"; c.doOutput = true
            c.setRequestProperty("Content-Type", "application/json")
            c.setRequestProperty("X-Zephyr-VLC-Remote-Token", token)
            // Connect explicitly so a dead PC (connect timeout) is told apart from an error answer.
            try { c.connect() } catch (e: java.net.SocketTimeoutException) { throw ConnectTimeout(NO_PC) }
            c.outputStream.use { it.write("{}".toByteArray(Charsets.UTF_8)) }
            if (c.responseCode == 401) throw BadToken("phone no longer paired")
            if (c.responseCode !in 200..299) {
                val detail = try {
                    c.errorStream?.use { JSONObject(it.readBytes().toString(Charsets.UTF_8)).optString("detail") }?.takeIf { it.isNotBlank() }
                } catch (e: Exception) { null }
                throw Unreachable(detail ?: "The PC could not report its volume (error ${c.responseCode}).")
            }
            val text = c.inputStream.use { s: InputStream -> s.readBytes().toString(Charsets.UTF_8) }
            val o = JSONObject(text)
            return o.getInt("volume") to o.getBoolean("mute")
        } catch (e: Unreachable) { throw e }
        catch (e: ConnectTimeout) { throw e }
        catch (e: BadToken) { throw e }
        catch (e: Exception) { throw Unreachable(NO_PC) }
        finally { releaseQuietly(c) }
    }

    fun volumeSet(host: String, port: Int, token: String, percent: Int) =
        post(host, port, "/volume/set", JSONObject().put("volume", percent), token)

    fun volumeMute(host: String, port: Int, token: String, mute: Boolean) =
        post(host, port, "/volume/mute", JSONObject().put("mute", mute), token)

    /** A named key: "enter", "backspace", "escape", "space", "tab", "up", "down", "left", "right". */
    fun key(host: String, port: Int, token: String, key: String) =
        post(host, port, "/key", JSONObject().put("key", key), token)

    /** Type literal text. */
    fun typeText(host: String, port: Int, token: String, text: String) =
        post(host, port, "/type", JSONObject().put("text", text), token)
}
