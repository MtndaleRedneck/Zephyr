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
class StreamServer(private val context: Context, private val wifiAddr: InetAddress, private val port: Int = 0,
    private val openDescriptor: (Uri, android.os.CancellationSignal) -> ParcelFileDescriptor? = { uri, signal -> context.contentResolver.openFileDescriptor(uri, "r", signal) },
) {

    data class Entry(val uri: Uri, val displayName: String, val subtitles: List<Uri>)

    private val random = SecureRandom()
    private val token: String = ByteArray(18).also(random::nextBytes).joinToString("") { "%02x".format(it) }
    private val entries = LinkedHashMap<String, Entry>()
    private var socket: ServerSocket? = null
    private val pool = Executors.newCachedThreadPool()
    private val permits = java.util.concurrent.Semaphore(24)
    private val mediaPermits = java.util.concurrent.Semaphore(8)
    private val perPeer = java.util.concurrent.ConcurrentHashMap<String,java.util.concurrent.atomic.AtomicInteger>()
    private val cancellations = java.util.concurrent.ConcurrentHashMap.newKeySet<android.os.CancellationSignal>()
    private val providers = java.util.concurrent.ConcurrentHashMap.newKeySet<Guarded<*>>()
    private val clientDeadlines = java.util.concurrent.ConcurrentHashMap<Socket, Long>()
    private val watchdog = Executors.newSingleThreadScheduledExecutor()
    private val clients = java.util.concurrent.ConcurrentHashMap.newKeySet<Socket>()
    private val requests = java.util.concurrent.ConcurrentHashMap.newKeySet<RequestState>()
    /** Providers abandoned at their deadline whose close has not completed yet.
     *  Caps new streams (503) so repeated provider hangs cannot spawn unbounded
     *  parked workers. Internal for the instrumented cap test. */
    internal val abandonedStreams = java.util.concurrent.atomic.AtomicInteger(0)
    /** Test seam: provider inactivity deadline in ms (production default 60 s). */
    @Volatile internal var providerDeadlineMs: Long = 60_000
    @Volatile private var running = false

    /** Serializes close() against every raw-descriptor use: while a worker holds
     *  the guard for one poll/read cycle, close() waits, so OUR close cannot slip
     *  between the worker obtaining the descriptor number and that cycle's use
     *  (descriptor reuse from our own close cannot feed bytes from the wrong file).
     *  Bounds: a ready poll does NOT bound the following Os.read - on a seekable
     *  SAF/FUSE provider the read itself can still block, and while it does, the
     *  close waits on the guard. The REQUEST no longer waits with it (M1: the
     *  deadline abandons the request without the guard); only the parked provider
     *  thread/FD remains until the read returns, bounded by MAX_ABANDONED. */
    private class Guarded<T : java.io.Closeable>(val inner: T) {
        private val gate = Object()
        @Volatile var closed = false; private set
        val closeQueued = java.util.concurrent.atomic.AtomicBoolean(false)
        @Volatile var deadlineAt: Long = Long.MAX_VALUE
        /** The request currently serving from this provider (deadline abandonment). */
        @Volatile var request: RequestState? = null
        private var onClosed: (() -> Unit)? = null
        fun close() {
            val cb: (() -> Unit)? = synchronized(gate) {
                if (!closed) { closed = true; runCatching { inner.close() } }
                val c = onClosed; onClosed = null; c
            }
            cb?.invoke()
        }
        /** Runs [cb] exactly once: immediately if already closed, otherwise when a
         *  future close() completes - possibly only after a parked provider read
         *  returns. Used to hand abandoned-worker capacity back. */
        fun onClosedOnce(cb: () -> Unit) {
            val runNow = synchronized(gate) { if (closed) true else { onClosed = cb; false } }
            if (runNow) cb()
        }
        fun <R> use(block: (T) -> R): R = synchronized(gate) {
            if (closed) throw java.io.InterruptedIOException("provider closed")
            block(inner)
        }
    }

    /** M1: per-request lifecycle arbitration. Exactly one of normal end (finish),
     *  provider-deadline abandonment (abandon), or stop wins the once-only release
     *  of the request's socket / open-stream count / permits - and that release
     *  never touches a provider guard, so a provider parked in an uninterruptible
     *  SAF/FUSE read cannot keep the request's socket, the open-stream count, or
     *  (through the idle policy reaching zero active) the service's wake/Wi-Fi
     *  locks. The parked provider thread/FD stays until the provider returns
     *  (declared residual), bounded by MAX_ABANDONED. */
    internal class RequestState(val client: Socket, val peer: String, private val release: (RequestState) -> Unit) {
        internal companion object { const val ACTIVE = 0; const val ABANDONED = 1; const val DONE = 2 }
        private val state = java.util.concurrent.atomic.AtomicInteger(ACTIVE)
        val isActive: Boolean get() = state.get() == ACTIVE
        fun abandon(): Boolean = if (state.compareAndSet(ACTIVE, ABANDONED)) { release(this); true } else false
        fun finish(): Boolean = if (state.compareAndSet(ACTIVE, DONE)) { release(this); true } else false
    }

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
            // Client sockets close inline: Socket.close never blocks.
            clientDeadlines.forEach { (client, at) -> if (now >= at && clientDeadlines.remove(client, at)) runCatching { client.close() } }
            // Provider closes run on the pool: a SAF/FUSE close can block, and one
            // blocked close must never stall the watchdog and every other expiry.
            providers.forEach { res ->
                if (now >= res.deadlineAt) {
                    // M1: abandon the REQUEST first, without the provider guard.
                    // Even when the worker is parked in an uninterruptible read
                    // (so the close below waits), its socket/count/permits release
                    // now, the idle policy can reach zero active, and the service's
                    // wake/Wi-Fi locks release. Capacity returns when the parked
                    // provider's close finally completes.
                    res.request?.let { req ->
                        if (req.abandon()) {
                            abandonedStreams.incrementAndGet()
                            res.onClosedOnce { abandonedStreams.decrementAndGet() }
                        }
                    }
                    if (res.closeQueued.compareAndSet(false, true)) {
                        try { pool.execute { providers.remove(res); res.close() } }
                        catch (e: java.util.concurrent.RejectedExecutionException) { providers.remove(res); res.close() }
                    }
                }
            }
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
                val req = RequestState(client, peer, ::releaseOnce)
                requests.add(req)
                try {
                    pool.execute {
                        // Once-only: if a deadline/stop abandoned this request
                        // first, finish() is a no-op - nothing releases twice.
                        try { runCatching { handle(client, req) } }
                        finally { req.finish(); requests.remove(req) }
                    }
                } catch (e: java.util.concurrent.RejectedExecutionException) {
                    req.finish(); requests.remove(req)
                }
            }
        }
    }

    private fun releasePeer(peer: String) {
        perPeer.computeIfPresent(peer) { _, count -> if (count.decrementAndGet() == 0) null else count }
    }

    /** The once-only request release (M1): client socket, open-stream count,
     *  peer count, permit. Runs on whichever side wins the arbitration - worker
     *  end, provider deadline, or stop - and never waits on a provider guard. */
    private fun releaseOnce(req: RequestState) {
        clients.remove(req.client)
        clientDeadlines.remove(req.client)
        openStreams.decrementAndGet()
        releasePeer(req.peer)
        permits.release()
        runCatching { req.client.close() }
    }

    fun stop() {
        running = false
        runCatching { socket?.close() }
        cancellations.forEach { it.cancel() }
        // Cheap, unblocking closes first: cutting client sockets frees worker
        // writes, and cancel signals free pending provider opens.
        clients.forEach { runCatching { it.close() } }
        // M1: stop arbitrates with deadline/end for every live request (once-only),
        // so a parked provider worker cannot keep counts held through teardown.
        requests.forEach { it.abandon() }
        watchdog.shutdownNow()
        pool.shutdownNow()
        kotlin.concurrent.thread(name = "stream-drain", isDaemon = true) {
            // Provider closes can block on a stuck SAF/FUSE provider: they belong
            // here, never on the caller (service shutdown / UI) thread.
            providers.forEach { it.close() }
            // Cleanup only after copy workers end; never delete while a worker can rename.
            while (!pool.awaitTermination(1, java.util.concurrent.TimeUnit.SECONDS)) { providers.forEach { it.close() } }
            // A process killed before this point leaves files behind; cleanCache()
            // on the next start() reclaims them.
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
        /** M1: at most this many providers may be parked past their deadline
         *  before new streams are refused with 503 (clear rejection instead of
         *  unbounded parked workers). Engineering choice: 4 pins at most 4
         *  threads + 4 provider FDs for a single-client (VLC) workload. */
        internal const val MAX_ABANDONED = 4
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

    private fun handle(client: Socket, req: RequestState) {
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
        // M1: repeated provider hangs park at most MAX_ABANDONED workers; further
        // streams are refused clearly instead of spawning unbounded parked workers.
        if (abandonedStreams.get() >= MAX_ABANDONED) return reply(client, 503, ByteArray(0), "text/plain")
        if (!mediaPermits.tryAcquire()) return reply(client, 503, ByteArray(0), "text/plain")
        try { client.soTimeout = 60_000; serve(client, uri, range, req) }
        finally { mediaPermits.release() }
    }

    /** Serve the document, honoring Range. Falls back to a cached copy when the
     *  provider's descriptor is not seekable. */
    private fun serve(client: Socket, uri: Uri, range: LongRange?, req: RequestState) {
        val signal = android.os.CancellationSignal()
        cancellations.add(signal)
        if (!running) { cancellations.remove(signal); return }
        val openDeadline = try { watchdog.schedule({ signal.cancel() }, 60, java.util.concurrent.TimeUnit.SECONDS) }
            catch (e: java.util.concurrent.RejectedExecutionException) { cancellations.remove(signal); return }
        val pfd: ParcelFileDescriptor = try { openDescriptor(uri, signal) } finally { openDeadline.cancel(false); cancellations.remove(signal) }
            ?: return reply(client, 404, ByteArray(0), "text/plain")
        if (!running) { pfd.close(); return }
        val guarded = Guarded(pfd)
        providers.add(guarded)
        if (!running) { providers.remove(guarded); guarded.close(); return }
        guarded.deadlineAt = android.os.SystemClock.elapsedRealtime() + providerDeadlineMs
        guarded.request = req
        try {
        run {
            val size = guarded.use { it.statSize }
            if (size < 0) {
                // This descriptor is only the seekability probe: close it BEFORE the
                // copy and the cached serve. Holding a pipe provider open while nobody
                // reads it can stall the provider's writer. The copy owns its own descriptor.
                providers.remove(guarded); guarded.close()
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
                                        val copyOpenDeadline = try { watchdog.schedule({ copySignal.cancel() }, 60, java.util.concurrent.TimeUnit.SECONDS) }
                                            catch (e: java.util.concurrent.RejectedExecutionException) { cancellations.remove(copySignal); throw java.io.InterruptedIOException() }
                                        val copyFd = try { openDescriptor(uri, copySignal) } finally { copyOpenDeadline.cancel(false); cancellations.remove(copySignal) }
                                            ?: throw java.io.IOException("no provider")
                                        if (!running) { copyFd.close(); throw java.io.InterruptedIOException() }
                                        val input = ParcelFileDescriptor.AutoCloseInputStream(copyFd)
                                        val ginput = Guarded<java.io.InputStream>(input)
                                        providers.add(ginput)
                                        if (!running) { providers.remove(ginput); ginput.close(); throw java.io.InterruptedIOException() }
                                        ginput.deadlineAt = android.os.SystemClock.elapsedRealtime() + providerDeadlineMs
                                        ginput.request = req
                                        object : java.io.FilterInputStream(input) {
                                            override fun read(b: ByteArray, off: Int, len: Int): Int {
                                                // Poll a non-seekable pipe instead of entering a blind read.
                                                // Each poll/read holds the guard for one cycle, so a deadline close
                                                // can only interpose between cycles; it cannot recycle the fd while
                                                // a cycle uses it. The ready poll bounds the WAIT for readability,
                                                // not the read that follows it (see Guarded).
                                                while (running) {
                                                    val ready = ginput.use {
                                                        val poll = android.system.StructPollfd().apply { this.fd = copyFd.fileDescriptor; events = android.system.OsConstants.POLLIN.toShort() }
                                                        try { android.system.Os.poll(arrayOf(poll), 500) > 0 }
                                                        catch (e: android.system.ErrnoException) { if (e.errno != android.system.OsConstants.EINTR) throw e; false }
                                                    }
                                                    if (ready) return ginput.use { raw ->
                                                        val n = raw.read(b, off, len)
                                                        ginput.deadlineAt = android.os.SystemClock.elapsedRealtime() + providerDeadlineMs
                                                        n
                                                    }
                                                }
                                                throw java.io.InterruptedIOException()
                                            }
                                            override fun close() { providers.remove(ginput); ginput.close() }
                                        }
                                    },
                                    tmp = tmp, final = cached,
                                    freeSpace = { context.cacheDir.usableSpace },
                                    maxBytes = MAX_CACHE_COPY_BYTES, minFree = MIN_FREE_BYTES, cancelled = { !running || !req.isActive },
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
            return serveFile(client, cached, cached.length(), range, req)
            }
            servedAny = true
            serveChannel(client, guarded, size, range, guessMime(uri), req)
        }
        } finally { providers.remove(guarded); guarded.close() }
    }

    private fun serveChannel(client: Socket, guarded: Guarded<ParcelFileDescriptor>, size: Long, range: LongRange?, mime: String, req: RequestState) {
        val (code, start, end) = resolveRange(size, range)
        val out = client.getOutputStream()
        headers(client, out, code, end - start + 1, mime, size, start, end)
        if (code == 416) { out.flush(); return }
        // The PFD remains the sole descriptor owner, including stop/watchdog close.
        // Avoid a FileInputStream wrapper that would need a second ownership policy.
        guarded.use { pfd ->
            while (true) {
                try { android.system.Os.lseek(pfd.fileDescriptor, start, android.system.OsConstants.SEEK_SET); break }
                catch (e: android.system.ErrnoException) { if (e.errno != android.system.OsConstants.EINTR) throw e }
            }
        }
        val buf = ByteArray(64 * 1024)
        var left = end - start + 1
        while (left > 0) {
            // Poll, then read: the poll bounds the WAIT for readability on a stuck
            // provider; it does not bound the Os.read that follows - a seekable
            // SAF/FUSE read can still block after a ready poll, and a deadline/stop
            // close then waits on the guard for that read. The request is already
            // abandoned and released by then (M1): only this parked thread/FD waits.
            // Our close cannot recycle the fd while a cycle holds the guard.
            val n = run {
                while (running) {
                    val ready = guarded.use { pfd ->
                        val poll = android.system.StructPollfd().apply { this.fd = pfd.fileDescriptor; events = android.system.OsConstants.POLLIN.toShort() }
                        try { android.system.Os.poll(arrayOf(poll), 500) > 0 }
                        catch (e: android.system.ErrnoException) { if (e.errno != android.system.OsConstants.EINTR) throw e; false }
                    }
                    if (ready) return@run guarded.use { pfd ->
                        var read: Int
                        while (true) {
                            try { read = android.system.Os.read(pfd.fileDescriptor, buf, 0, minOf(buf.size.toLong(), left).toInt()); break }
                            catch (e: android.system.ErrnoException) {
                                if (e.errno != android.system.OsConstants.EINTR) throw EOFException().apply { initCause(e) }
                            }
                        }
                        read
                    }
                }
                throw java.io.InterruptedIOException()
            }
            if (n <= 0) throw EOFException()
            // Abandoned while the provider was parked: never write to the
            // (closed) socket afterwards.
            if (!req.isActive) throw java.io.InterruptedIOException("request abandoned")
            guarded.deadlineAt = android.os.SystemClock.elapsedRealtime() + providerDeadlineMs
            clientDeadlines[client] = android.os.SystemClock.elapsedRealtime() + 60_000
            out.write(buf, 0, n)
            clientDeadlines.remove(client)
            left -= n
        }
        out.flush()
    }

    private fun serveFile(client: Socket, f: java.io.File, size: Long, range: LongRange?, req: RequestState) {
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
                if (!req.isActive) throw java.io.InterruptedIOException("request abandoned")
                clientDeadlines[client] = android.os.SystemClock.elapsedRealtime() + 60_000
                out.write(buf, 0, n)
                clientDeadlines.remove(client)
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
        clientDeadlines[client] = android.os.SystemClock.elapsedRealtime() + 60_000
        try { out.write(sb.toString().toByteArray(Charsets.US_ASCII)) } finally { clientDeadlines.remove(client) }
    }

    private fun reply(client: Socket, code: Int, body: ByteArray, mime: String) {
        val out = client.getOutputStream()
        headers(client, out, code, body.size.toLong(), mime, body.size.toLong(), 0, body.size - 1L)
        out.write(body); out.flush()
    }

    private fun guessMime(uri: Uri): String =
        context.contentResolver.getType(uri) ?: "video/*"
}
