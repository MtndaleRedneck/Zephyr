package com.zephyr.remote

import android.content.Context
import android.net.Uri
import android.os.ParcelFileDescriptor
import java.io.EOFException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.SecureRandom
import java.util.concurrent.Executors

/**
 * Serves the original movie file (no re-encode) to the Zephyr Player over plain HTTP.
 *
 * URL shape: http://<wifi-ip>:<port>/s/<sessionToken>/<id>[/sub/<n>]
 *  - Session token is random per serving session and dies with it; the pairing
 *    token is never used here (libVLC can't set custom headers, so the token
 *    rides in the path).
 *  - Range requests get proper 206 + Content-Range so libVLC can seek.
 *  - SAF content URIs are served directly via openFileDescriptor; if a provider
 *    hands back a non-seekable (pipe) descriptor we fall back to copying the file
 *    into the cache once and serving that.
 *  - Binds to the Wi-Fi interface only.
 *
 * Prototype status: platform-verification list still open (SAF seekability across
 * providers, service type, Wi-Fi lock behavior). Nothing here is final.
 */
class StreamServer(private val context: Context, private val wifiAddr: InetAddress, private val port: Int = 0) {

    data class Entry(val uri: Uri, val displayName: String, val subtitles: List<Uri>)

    private val random = SecureRandom()
    private val token: String = ByteArray(18).also(random::nextBytes).joinToString("") { "%02x".format(it) }
    private val entries = LinkedHashMap<String, Entry>()
    private var socket: ServerSocket? = null
    private val pool = Executors.newCachedThreadPool()
    @Volatile private var running = false

    val baseUrl: String get() = "http://${wifiAddr.hostAddress}:${socket?.localPort ?: port}/s/$token"

    fun addMovie(uri: Uri, name: String, subtitles: List<Uri> = emptyList()): String {
        val id = (entries.size + 1).toString()
        entries[id] = Entry(uri, name, subtitles)
        return "$baseUrl/$id"
    }

    fun start() {
        val srv = ServerSocket(port, 8, wifiAddr)
        socket = srv
        running = true
        pool.execute {
            while (running) {
                val client = runCatching { srv.accept() }.getOrNull() ?: break
                // A stalled client must not hold a worker forever (teardown C12).
                runCatching { client.soTimeout = 60_000 }
                openStreams.incrementAndGet()
                pool.execute {
                    try { runCatching { handle(client) } }
                    finally { openStreams.decrementAndGet(); runCatching { client.close() } }
                }
            }
        }
    }

    fun stop() {
        running = false
        runCatching { socket?.close() }
        pool.shutdownNow()
    }

    /** Open client connections right now (teardown C1: an open connection means the
     *  movie is alive - a long pause keeps the socket and must never cut the stream). */
    /** Incremented on the accept thread, decremented on pool threads: an
     *  AtomicInteger, because @Volatile does not make ++/-- atomic and a drifted
     *  count would stall (or rush) the idle watchdog (round-3 A4). */
    val openStreams = java.util.concurrent.atomic.AtomicInteger(0)
    /** True once the first request was served: the shutdown watchdog arms only after this. */
    @Volatile var servedAny = false

    /** S9: one in-flight cache copy per URI. */
    private val copyLocks = java.util.concurrent.ConcurrentHashMap<String, Object>()

    companion object {
        /** Cap for one non-seekable file copied into cache (teardown C12). */
        const val MAX_CACHE_COPY_BYTES = 8L * 1024 * 1024 * 1024
        /** S9: refuse a cache copy the phone cannot hold (floor for system + VLC). */
        const val MIN_FREE_BYTES = 512L * 1024 * 1024
        /** Drop every cached stream copy (called when the service stops). */
        private fun cacheFile(context: android.content.Context, uri: Uri, suffix: String): java.io.File {
            val hex = java.security.MessageDigest.getInstance("SHA-256")
                .digest(uri.toString().toByteArray(Charsets.UTF_8))
                .joinToString("") { "%02x".format(it) }
            return java.io.File(context.cacheDir, "stream-$hex$suffix")
        }

        fun cleanCache(context: android.content.Context) {
            context.cacheDir.listFiles()?.filter { it.name.startsWith("stream-") }?.forEach { it.delete() }
        }
    }

    private fun handle(client: Socket) {
        val input = client.getInputStream().bufferedReader()
        val requestLine = input.readLine() ?: return
        val parts = requestLine.split(" ")
        if (parts.size < 2 || parts[0] != "GET") return
        var range: LongRange? = null
        while (true) {
            val line = input.readLine() ?: return
            if (line.isEmpty()) break
            if (line.startsWith("Range:", ignoreCase = true)) {
                // bytes=a-b | bytes=a- | bytes=-b (suffix not supported -> ignore)
                val spec = line.substringAfter('=').trim()
                val (a, b) = spec.removePrefix("bytes=").split('-').let { it[0] to it.getOrElse(1) { "" } }
                val start = a.toLongOrNull()
                if (start != null) range = start..(b.toLongOrNull() ?: Long.MAX_VALUE)
            }
        }
        val path = parts[1].removePrefix("/s/$token/")
        if (path == parts[1]) return reply(client, 403, "bad token".toByteArray(), "text/plain")
        val segs = path.split('/')
        val entry = entries[segs.getOrNull(0)] ?: return reply(client, 404, ByteArray(0), "text/plain")
        val uri: Uri = when {
            segs.size == 1 -> entry.uri
            segs.size == 3 && segs[1] == "sub" -> entry.subtitles.getOrNull(segs[2].toIntOrNull() ?: -1)
                ?: return reply(client, 404, ByteArray(0), "text/plain")
            else -> return reply(client, 404, ByteArray(0), "text/plain")
        }
        serve(client, uri, range)
    }

    /** Serve the document, honoring Range. Falls back to a cached copy when the
     *  provider's descriptor is not seekable. */
    private fun serve(client: Socket, uri: Uri, range: LongRange?) {
        val pfd: ParcelFileDescriptor = context.contentResolver.openFileDescriptor(uri, "r")
            ?: return reply(client, 404, ByteArray(0), "text/plain")
        pfd.use { fd ->
            val size = fd.statSize
            if (size < 0) {
                // Non-seekable provider: copy once into cache, then serve the copy.
                // Teardown C12: the copy lands under a temp name and is renamed only when
                // COMPLETE, so a cut-off copy is never reused; oversized copies are capped.
                // N-4: SHA-256 of the URI - a 32-bit hashCode collision between two
                // different files must never share one cache slot.
                val cached = cacheFile(context, uri, "")
                if (!cached.exists()) {
                    // S9: one copy per URI at a time - two first requests for the same
                    // non-seekable file must not write the same .part together. The
                    // second request waits, then serves the finished copy.
                    val lock = copyLocks.getOrPut(uri.toString()) { Object() }
                    synchronized(lock) {
                    if (!cached.exists()) {
                    val tmp = cacheFile(context, uri, ".part")
                    try {
                        CacheCopier.copy(
                            open = { context.contentResolver.openInputStream(uri) },
                            tmp = tmp, final = cached,
                            freeSpace = { context.cacheDir.usableSpace },
                            maxBytes = MAX_CACHE_COPY_BYTES, minFree = MIN_FREE_BYTES,
                        )
                    } catch (e: CacheCopier.NoSpace) {
                        return reply(client, 507, ByteArray(0), "text/plain")
                    } catch (e: CacheCopier.TooLarge) {
                        return reply(client, 413, ByteArray(0), "text/plain")
                    } catch (e: Exception) {
                        return reply(client, 500, ByteArray(0), "text/plain")
                    }
                    }
                    }
                }
                // S-2: "served" means a valid /s/<token>/<id> request actually starting
            // to send bytes - not any stray request line. Arming on junk (bad token,
            // 404 path, port scan) would strand the service on the served rule forever.
            servedAny = true
            return serveFile(client, cached, cached.length(), range)
            }
            servedAny = true
            serveChannel(client, pfd, size, range, guessMime(uri))
        }
    }

    private fun serveChannel(client: Socket, pfd: ParcelFileDescriptor, size: Long, range: LongRange?, mime: String) {
        val channel = java.io.FileInputStream(pfd.fileDescriptor).channel
        val (code, start, end) = resolveRange(size, range)
        val out = client.getOutputStream()
        headers(out, code, end - start + 1, mime, size, start, end)
        channel.position(start)
        val buf = java.nio.ByteBuffer.allocate(64 * 1024)
        var left = end - start + 1
        while (left > 0) {
            buf.clear()
            if (left < buf.capacity()) buf.limit(left.toInt())
            val n = try { channel.read(buf) } catch (e: Exception) { -1 }
            if (n <= 0) throw EOFException()
            out.write(buf.array(), 0, n)
            left -= n
        }
        out.flush()
    }

    private fun serveFile(client: Socket, f: java.io.File, size: Long, range: LongRange?) {
        java.io.RandomAccessFile(f, "r").use { raf ->
            val (code, start, end) = resolveRange(size, range)
            val out = client.getOutputStream()
            headers(out, code, end - start + 1, "video/*", size, start, end)
            raf.seek(start)
            val buf = ByteArray(64 * 1024)
            var left = end - start + 1
            while (left > 0) {
                val n = raf.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                if (n <= 0) break
                out.write(buf, 0, n)
                left -= n
            }
            out.flush()
        }
    }

    internal fun resolveRange(size: Long, range: LongRange?): Triple<Int, Long, Long> {
        if (range == null) return Triple(200, 0L, size - 1)
        val start = range.first.coerceIn(0, size - 1)
        val end = minOf(range.last, size - 1)
        return Triple(206, start, end)
    }

    private fun headers(out: java.io.OutputStream, code: Int, len: Long, mime: String, total: Long, start: Long, end: Long) {
        val sb = StringBuilder()
        sb.append("HTTP/1.1 ").append(code).append(if (code == 206) " Partial Content" else if (code == 200) " OK" else " Error").append("\r\n")
        sb.append("Content-Length: ").append(len).append("\r\n")
        sb.append("Content-Type: ").append(mime).append("\r\n")
        sb.append("Accept-Ranges: bytes\r\n")
        if (code == 206) sb.append("Content-Range: bytes ").append(start).append('-').append(end).append('/').append(total).append("\r\n")
        sb.append("Connection: close\r\n\r\n")
        out.write(sb.toString().toByteArray(Charsets.US_ASCII))
    }

    private fun reply(client: Socket, code: Int, body: ByteArray, mime: String) {
        val out = client.getOutputStream()
        headers(out, code, body.size.toLong(), mime, body.size.toLong(), 0, body.size - 1L)
        out.write(body); out.flush()
    }

    private fun guessMime(uri: Uri): String =
        context.contentResolver.getType(uri) ?: "video/*"
}
