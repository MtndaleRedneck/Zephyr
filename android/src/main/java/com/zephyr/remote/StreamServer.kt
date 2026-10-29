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
    private val permits = java.util.concurrent.Semaphore(24)
    private val mediaPermits = java.util.concurrent.Semaphore(8)
    private val perPeer = java.util.concurrent.ConcurrentHashMap<String,java.util.concurrent.atomic.AtomicInteger>()
    private val providers = java.util.concurrent.ConcurrentHashMap.newKeySet<java.io.Closeable>()
    private val cancellations = java.util.concurrent.ConcurrentHashMap.newKeySet<android.os.CancellationSignal>()
    private val deadlines = java.util.concurrent.ConcurrentHashMap<java.io.Closeable, Long>()
    private val watchdog = Executors.newSingleThreadScheduledExecutor()
    private val clients = java.util.concurrent.ConcurrentHashMap.newKeySet<Socket>()
    @Volatile private var running = false

    val baseUrl: String get() = "http://${wifiAddr.hostAddress}:${socket?.localPort ?: port}/s/$token"

    fun addMovie(uri: Uri, name: String, subtitles: List<Uri> = emptyList()): String {
        val id = (entries.size + 1).toString()
        entries[id] = Entry(uri, name, subtitles)
        return "$baseUrl/$id"
    }

    fun start() {
        activeTokens.add(token)
        cleanCache(context)
        val srv = ServerSocket(port, 8, wifiAddr)
        socket = srv
        running = true
        watchdog.scheduleWithFixedDelay({
            val now = android.os.SystemClock.elapsedRealtime()
            deadlines.forEach { resource, at -> if (now >= at && deadlines.remove(resource, at)) runCatching { resource.close() } }
        }, 1, 1, java.util.concurrent.TimeUnit.SECONDS)
        pool.execute {
            while (running) {
                val client = runCatching { srv.accept() }.getOrNull() ?: break
                val peer = client.inetAddress.hostAddress.orEmpty()
                var count = 0
                perPeer.compute(peer) { _, current -> (current ?: java.util.concurrent.atomic.AtomicInteger(0)).also { count = it.incrementAndGet() } }
                if (count > 4) { releasePeer(peer); runCatching { client.close() }; continue }
                if (!permits.tryAcquire()) { releasePeer(peer); runCatching { client.close() }; continue }
                // A stalled client must not hold a worker forever (teardown C12).
                runCatching { client.soTimeout = 60_000 }
                clients.add(client)
                openStreams.incrementAndGet()
                try {
                    pool.execute {
                        try { runCatching { handle(client) } }
                        finally { clients.remove(client); deadlines.remove(client); openStreams.decrementAndGet(); releasePeer(peer); permits.release(); runCatching { client.close() } }
                    }
                } catch (e: java.util.concurrent.RejectedExecutionException) {
                    clients.remove(client); deadlines.remove(client); openStreams.decrementAndGet(); releasePeer(peer); permits.release(); runCatching { client.close() }
                }
            }
        }
    }

    private fun releasePeer(peer: String) {
        perPeer.computeIfPresent(peer) { _, count -> if (count.decrementAndGet() == 0) null else count }
    }

    fun stop() {
        running = false
        runCatching { socket?.close() }
        cancellations.forEach { it.cancel() }
        providers.forEach { runCatching { it.close() } }
        clients.forEach { runCatching { it.close() } }
        watchdog.shutdownNow()
        pool.shutdownNow()
        // Cleanup only after copy workers end; never delete while a worker can rename.
        kotlin.concurrent.thread(name = "stream-drain", isDaemon = true) {
            while (!pool.awaitTermination(1, java.util.concurrent.TimeUnit.SECONDS)) { providers.forEach { runCatching { it.close() } } }
            context.cacheDir.listFiles()?.filter { it.name.startsWith("stream-$token-") }?.forEach { it.delete() }
            activeTokens.remove(token)
        }
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
        private val activeTokens = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
        /** Cap for one non-seekable file copied into cache (teardown C12). */
        const val MAX_CACHE_COPY_BYTES = 8L * 1024 * 1024 * 1024
        /** S9: refuse a cache copy the phone cannot hold (floor for system + VLC). */
        const val MIN_FREE_BYTES = 512L * 1024 * 1024
        /** The request-line gate: anything that is not "GET <path> ..." is refused
         *  before any serving happens - garbage must never flip servedAny. */
        internal fun validRequestLine(line: String): Boolean {
            val parts = line.split(" ")
            return parts.size >= 2 && parts[0] == "GET"
        }

        /** The cache slot for one streamed URI (temp suffix while a copy is in flight). */
        private fun cacheFile(context: android.content.Context, uri: Uri, suffix: String): java.io.File {
            val hex = java.security.MessageDigest.getInstance("SHA-256")
                .digest(uri.toString().toByteArray(Charsets.UTF_8))
                .joinToString("") { "%02x".format(it) }
            return java.io.File(context.cacheDir, "stream-$hex$suffix")
        }

        /** Drop every cached stream copy (called when the service stops). */
        fun cleanCache(context: android.content.Context) {
            context.cacheDir.listFiles()?.filter { file -> file.name.startsWith("stream-") && activeTokens.none { file.name.startsWith("stream-$it-") } }?.forEach { it.delete() }
        }
    }

    private fun handle(client: Socket) {
        val input = client.getInputStream().bufferedReader()
        val deadline = android.os.SystemClock.elapsedRealtime() + 10_000
        fun line(): String? {
            val b = StringBuilder()
            while (b.length < 8192) {
                val left = deadline - android.os.SystemClock.elapsedRealtime()
                if (left <= 0) throw java.net.SocketTimeoutException()
                client.soTimeout = minOf(left, 10_000L).toInt()
                val c = input.read(); if (c < 0) return null
                if (c == 10) return b.toString().trimEnd('\r')
                b.append(c.toChar())
            }
            throw java.io.IOException("header line too long")
        }
        val requestLine = line() ?: return
        if (!validRequestLine(requestLine)) return
        val parts = requestLine.split(" ")
        var range: LongRange? = null
        var headerBytes = requestLine.length
        var headerCount = 0
        while (true) {
            val line = line() ?: return
            headerBytes += line.length
            if (++headerCount > 64 || headerBytes > 32768) return
            if (line.isEmpty()) break
            if (line.startsWith("Range:", ignoreCase = true)) {
                range = HttpRange.parse(line.substringAfter(':').trim())
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
        if (!mediaPermits.tryAcquire()) return reply(client, 503, ByteArray(0), "text/plain")
        try { client.soTimeout = 60_000; serve(client, uri, range) }
        finally { mediaPermits.release() }
    }

    /** Serve the document, honoring Range. Falls back to a cached copy when the
     *  provider's descriptor is not seekable. */
    private fun serve(client: Socket, uri: Uri, range: LongRange?) {
        val signal = android.os.CancellationSignal()
        cancellations.add(signal)
        if (!running) { cancellations.remove(signal); return }
        val openDeadline = watchdog.schedule({ signal.cancel() }, 60, java.util.concurrent.TimeUnit.SECONDS)
        val pfd: ParcelFileDescriptor = try { context.contentResolver.openFileDescriptor(uri, "r", signal) } finally { openDeadline.cancel(false); cancellations.remove(signal) }
            ?: return reply(client, 404, ByteArray(0), "text/plain")
        if (!running) { pfd.close(); return }
        providers.add(pfd)
        if (!running) { providers.remove(pfd); pfd.close(); return }
        deadlines[pfd] = android.os.SystemClock.elapsedRealtime() + 60_000
        try {
        pfd.use { fd ->
            val size = fd.statSize
            if (size < 0) {
                deadlines.remove(pfd) // This descriptor is only the seekability probe; the copy owns its own stall timer.
                // Non-seekable provider: copy once into cache, then serve the copy.
                // Teardown C12: the copy lands under a temp name and is renamed only when
                // COMPLETE, so a cut-off copy is never reused; oversized copies are capped.
                // N-4: SHA-256 of the URI - a 32-bit hashCode collision between two
                // different files must never share one cache slot.
                val cached = java.io.File(context.cacheDir, "stream-$token-" + cacheFile(context, uri, "").name)
                if (!cached.exists()) {
                    // S9: one copy per URI at a time - two first requests for the same
                    // non-seekable file must not write the same .part together. The
                    // second request waits, then serves the finished copy.
                    val lock = copyLocks.getOrPut(uri.toString()) { Object() }
                    synchronized(lock) {
                        if (!cached.exists()) {
                            val tmp = java.io.File(context.cacheDir, "stream-$token-" + cacheFile(context, uri, ".part").name)
                            try {
                                CacheCopier.copy(
                                    open = {
                                        val copySignal = android.os.CancellationSignal(); cancellations.add(copySignal)
                                        val copyOpenDeadline = watchdog.schedule({ copySignal.cancel() }, 60, java.util.concurrent.TimeUnit.SECONDS)
                                        val copyFd = try { context.contentResolver.openFileDescriptor(uri, "r", copySignal) } finally { copyOpenDeadline.cancel(false); cancellations.remove(copySignal) }
                                            ?: throw java.io.IOException("no provider")
                                        if (!running) { copyFd.close(); throw java.io.InterruptedIOException() }
                                        val input = ParcelFileDescriptor.AutoCloseInputStream(copyFd)
                                        providers.add(input)
                                        if (!running) { providers.remove(input); input.close(); throw java.io.InterruptedIOException() }
                                        deadlines[input] = android.os.SystemClock.elapsedRealtime() + 60_000
                                        object : java.io.FilterInputStream(input) {
                                            override fun read(b: ByteArray, off: Int, len: Int): Int {
                                                // Poll a non-seekable pipe instead of entering an uninterruptible read.
                                                val poll = android.system.StructPollfd().apply { this.fd = copyFd.fileDescriptor; events = android.system.OsConstants.POLLIN.toShort() }
                                                while (running) {
                                                    try { if (android.system.Os.poll(arrayOf(poll), 500) > 0) break }
                                                    catch (e: android.system.ErrnoException) { if (e.errno != android.system.OsConstants.EINTR) throw e }
                                                }
                                                if (!running) throw java.io.InterruptedIOException()
                                                val n = super.read(b, off, len)
                                                deadlines[input] = android.os.SystemClock.elapsedRealtime() + 60_000
                                                return n
                                            }
                                            override fun close() { providers.remove(input); deadlines.remove(input); super.close() }
                                        }
                                    },
                                    tmp = tmp, final = cached,
                                    freeSpace = { context.cacheDir.usableSpace },
                                    maxBytes = MAX_CACHE_COPY_BYTES, minFree = MIN_FREE_BYTES, cancelled = { !running },
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
        } finally { providers.remove(pfd); deadlines.remove(pfd) }
    }

    private fun serveChannel(client: Socket, pfd: ParcelFileDescriptor, size: Long, range: LongRange?, mime: String) {
        val channel = java.io.FileInputStream(pfd.fileDescriptor).channel
        val (code, start, end) = resolveRange(size, range)
        val out = client.getOutputStream()
        headers(client, out, code, end - start + 1, mime, size, start, end)
        if (code == 416) { out.flush(); return }
        channel.position(start)
        val buf = java.nio.ByteBuffer.allocate(64 * 1024)
        var left = end - start + 1
        while (left > 0) {
            buf.clear()
            if (left < buf.capacity()) buf.limit(left.toInt())
            deadlines[pfd] = android.os.SystemClock.elapsedRealtime() + 60_000
            val n = try { channel.read(buf) } catch (e: Exception) { -1 }
            if (n <= 0) throw EOFException()
            deadlines[client] = android.os.SystemClock.elapsedRealtime() + 60_000
            out.write(buf.array(), 0, n)
            deadlines.remove(client)
            deadlines.remove(pfd)
            left -= n
        }
        out.flush()
    }

    private fun serveFile(client: Socket, f: java.io.File, size: Long, range: LongRange?) {
        java.io.RandomAccessFile(f, "r").use { raf ->
            val (code, start, end) = resolveRange(size, range)
            val out = client.getOutputStream()
            headers(client, out, code, end - start + 1, "video/*", size, start, end)
            if (code == 416) { out.flush(); return }
            raf.seek(start)
            val buf = ByteArray(64 * 1024)
            var left = end - start + 1
            while (left > 0) {
                val n = raf.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                if (n <= 0) break
                deadlines[client] = android.os.SystemClock.elapsedRealtime() + 60_000
                out.write(buf, 0, n)
                deadlines.remove(client)
                left -= n
            }
            out.flush()
        }
    }

    internal fun resolveRange(size: Long, range: LongRange?): Triple<Int, Long, Long> {
        return HttpRange.resolve(size, range)
    }

    private fun headers(client: Socket, out: java.io.OutputStream, code: Int, len: Long, mime: String, total: Long, start: Long, end: Long) {
        val sb = StringBuilder()
        sb.append("HTTP/1.1 ").append(code).append(if (code == 206) " Partial Content" else if (code == 200) " OK" else if (code == 416) " Range Not Satisfiable" else " Error").append("\r\n")
        sb.append("Content-Length: ").append(len).append("\r\n")
        sb.append("Content-Type: ").append(mime).append("\r\n")
        sb.append("Accept-Ranges: bytes\r\n")
        if (code == 416) sb.append("Content-Range: bytes */").append(total).append("\r\n")
        if (code == 206) sb.append("Content-Range: bytes ").append(start).append('-').append(end).append('/').append(total).append("\r\n")
        sb.append("Connection: close\r\n\r\n")
        deadlines[client] = android.os.SystemClock.elapsedRealtime() + 60_000
        try { out.write(sb.toString().toByteArray(Charsets.US_ASCII)) } finally { deadlines.remove(client) }
    }

    private fun reply(client: Socket, code: Int, body: ByteArray, mime: String) {
        val out = client.getOutputStream()
        headers(client, out, code, body.size.toLong(), mime, body.size.toLong(), 0, body.size - 1L)
        out.write(body); out.flush()
    }

    private fun guessMime(uri: Uri): String =
        context.contentResolver.getType(uri) ?: "video/*"
}
