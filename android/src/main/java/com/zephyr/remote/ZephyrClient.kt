package com.zephyr.remote

import java.util.concurrent.TimeUnit
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject

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
        val found = LinkedHashMap<String, com.bingemote.DiscoveredHelper>()
        // Legacy first, then current: if both generations somehow answer for one
        // host, the 1.2+ player wins.
        for (h in com.bingemote.HelperClient.discoverRaw(REPLY_MAGIC, waitMs, targets, LEGACY_DISCOVERY_PORT)) found[h.host] = h
        for (h in com.bingemote.HelperClient.discoverRaw(REPLY_MAGIC, waitMs, targets, DISCOVERY_PORT)) found[h.host] = h
        return found.values.toList()
    }

    /** POST /pair - trade the 6-digit code on the player screen for a pairing token.
     *  The one unauthenticated call; everything after rides on the returned token. */
    fun pair(host: String, port: Int, code: String): String {
        val body = JSONObject().put("code", code).toString()
            .toRequestBody("application/json".toMediaType())
        val req = Request.Builder().url("http://$host:$port/pair").post(body).build()
        client.newCall(req).execute().use { resp ->
            if (resp.code == 403) throw java.io.IOException("Wrong code - check the code on the player screen.")
            if (resp.code == 429) throw java.io.IOException("Too many wrong codes. Wait a few minutes, then try the fresh code on the PC screen.")
            if (resp.code == 409) throw java.io.IOException("This PC already has too many phones paired - remove one first.")
            if (!resp.isSuccessful) throw java.io.IOException("Player returned ${resp.code}")
            return JSONObject(resp.body!!.string()).getString("token")
        }
    }

    private fun authReq(host: String, port: Int, token: String, path: String): Request.Builder =
        Request.Builder().url("http://$host:$port$path").header("Authorization", "Bearer $token")

    /** The PC removed this phone's pairing: distinct from an outage so the app can say "pair again". */
    class BadToken : java.io.IOException("This phone isn't paired with Zephyr Player anymore. Pair again in Settings.")

    private fun post(host: String, port: Int, token: String, path: String, json: JSONObject? = null) {
        val req = authReq(host, port, token, path)
            .post((json ?: JSONObject()).toString().toRequestBody("application/json".toMediaType()))
            .build()
        client.newCall(req).execute().use { resp ->
            if (resp.code == 401) throw BadToken()
            if (!resp.isSuccessful) throw java.io.IOException("Player returned ${resp.code}")
        }
    }

    private fun get(host: String, port: Int, token: String, path: String): JSONObject {
        client.newCall(authReq(host, port, token, path).get().build()).execute().use { resp ->
            if (resp.code == 401) throw BadToken()
            if (!resp.isSuccessful) throw java.io.IOException("Player returned ${resp.code}")
            return JSONObject(resp.body!!.string())
        }
    }

    // ---- Library (protocol.md Library) ----

    data class LibraryEntry(val name: String, val dir: Boolean)

    /** GET /library/list?path=rel -> one level of the assigned folder, folders first. */
    fun libraryList(host: String, port: Int, token: String, path: String = ""): Pair<Boolean, List<LibraryEntry>> {
        val json = get(host, port, token, "/library/list?path=" + java.net.URLEncoder.encode(path, "UTF-8"))
        val entries = json.getJSONArray("entries")
        val out = ArrayList<LibraryEntry>(entries.length())
        for (i in 0 until entries.length()) {
            val e = entries.getJSONObject(i)
            out.add(LibraryEntry(e.getString("name"), e.getBoolean("dir")))
        }
        return json.optBoolean("atRoot", true) to out
    }

    /** GET /library/browse?path=abs -> folder-picker listing (drives or subfolders). */
    fun libraryBrowse(host: String, port: Int, token: String, path: String = ""): List<LibraryEntry> {
        val json = get(host, port, token, "/library/browse?path=" + java.net.URLEncoder.encode(path, "UTF-8"))
        val entries = json.getJSONArray("entries")
        val out = ArrayList<LibraryEntry>(entries.length())
        for (i in 0 until entries.length()) {
            val e = entries.getJSONObject(i)
            out.add(LibraryEntry(e.getString("name"), e.getBoolean("dir")))
        }
        return out
    }

    fun libraryFolder(host: String, port: Int, token: String): String =
        get(host, port, token, "/library/folder").optString("libraryFolder", "")

    fun setLibraryFolder(host: String, port: Int, token: String, folder: String) {
        post(host, port, token, "/library/folder", JSONObject().put("folder", folder))
    }

    // ---- Control (protocol.md Control) ----

    fun pause(host: String, port: Int, token: String) = post(host, port, token, "/control/pause")
    fun resume(host: String, port: Int, token: String) = post(host, port, token, "/control/resume")
    fun seek(host: String, port: Int, token: String, positionSec: Long) =
        post(host, port, token, "/control/seek", JSONObject().put("position", positionSec))
    fun volume(host: String, port: Int, token: String, volume: Int) =
        post(host, port, token, "/control/volume", JSONObject().put("volume", volume))
    fun mute(host: String, port: Int, token: String, mute: Boolean) =
        post(host, port, token, "/control/mute", JSONObject().put("mute", mute))
    fun captions(host: String, port: Int, token: String, track: Int) =
        post(host, port, token, "/control/captions", JSONObject().put("track", track))
    fun fullscreen(host: String, port: Int, token: String, on: Boolean) =
        post(host, port, token, "/control/fullscreen", JSONObject().put("on", on))
    fun controlStatus(host: String, port: Int, token: String): JSONObject =
        get(host, port, token, "/control/status")

    // ---- Input (protocol.md Input receiver) ----

    fun inputMove(host: String, port: Int, token: String, dx: Double, dy: Double) =
        post(host, port, token, "/input/move", JSONObject().put("dx", dx).put("dy", dy))
    fun inputButton(host: String, port: Int, token: String, button: String, down: Boolean) =
        post(host, port, token, "/input/button", JSONObject().put("button", button).put("down", down))
    fun inputScroll(host: String, port: Int, token: String, dy: Double) =
        post(host, port, token, "/input/scroll", JSONObject().put("dy", dy))
    fun inputZoom(host: String, port: Int, token: String, delta: Double) =
        post(host, port, token, "/input/zoom", JSONObject().put("delta", delta))
    fun inputKey(host: String, port: Int, token: String, key: String) =
        post(host, port, token, "/input/key", JSONObject().put("key", key))
    fun inputType(host: String, port: Int, token: String, text: String) =
        post(host, port, token, "/input/type", JSONObject().put("text", text))

    /** POST /play - ask the player to open the stream. */
    fun play(host: String, port: Int, token: String, url: String, subtitles: List<String>, position: Long? = null) {
        val body = JSONObject()
            .put("url", url)
            .put("subtitles", JSONArray(subtitles))
            .apply { if (position != null) put("position", position) }
            .toString()
            .toRequestBody("application/json".toMediaType())
        val req = Request.Builder()
            .url("http://$host:$port/play")
            .header("Authorization", "Bearer $token")
            .post(body)
            .build()
        client.newCall(req).execute().use { resp ->
            if (resp.code == 401) throw BadToken()
            if (!resp.isSuccessful) throw java.io.IOException("Player returned ${resp.code}")
        }
    }

    /** /control/status's subtitle track list (PC 1.2+); empty from older players. */
    fun parseSpuTracks(json: JSONObject): List<com.bingemote.SubTrack> {
        val current = json.optInt("spu", -1)
        val arr = json.optJSONArray("spuTracks") ?: return emptyList()
        val out = ArrayList<com.bingemote.SubTrack>(arr.length())
        for (i in 0 until arr.length()) {
            val t = arr.getJSONObject(i)
            val id = t.getInt("id")
            out.add(com.bingemote.SubTrack(id, t.optString("name", "Track ${i + 1}"), id == current))
        }
        return out
    }
}
