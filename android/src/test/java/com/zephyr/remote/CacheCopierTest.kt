package com.zephyr.remote

import java.io.File
import kotlin.concurrent.thread
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** S9: the cache copy policy under concurrency, failure and shrinking space. */
class CacheCopierTest {

    private fun dir(): File {
        val d = File(System.getProperty("java.io.tmpdir"), "cachecopier-" + System.nanoTime())
        d.mkdirs(); return d
    }

    @Test fun partialCopyIsNeverReused() {
        val d = dir(); val tmp = File(d, "x.part"); val fin = File(d, "x")
        val broken = object : java.io.InputStream() {
            var n = 0
            override fun read(): Int = if (n++ < 100) 65 else throw java.io.IOException("cut off")
        }
        try { CacheCopier.copy({ broken }, tmp, fin, { 1L shl 40 }, 1L shl 33, 0) }
        catch (e: Exception) { /* expected */ }
        assertFalse("partial .part deleted", tmp.exists())
        assertFalse("partial never renamed", fin.exists())
    }

    @Test fun completeCopyRenames() {
        val d = dir(); val tmp = File(d, "x.part"); val fin = File(d, "x")
        CacheCopier.copy({ "hello".byteInputStream() }, tmp, fin, { 1L shl 40 }, 1L shl 33, 0)
        assertTrue(fin.exists()); assertFalse(tmp.exists())
        assertEquals("hello", fin.readText())
    }

    @Test fun freeSpaceIsEnforcedAsBytesGrow() {
        val d = dir(); val tmp = File(d, "x.part"); val fin = File(d, "x")
        var free = 10_000_000L
        // 1 MB of data; the floor is 5 MB and the "disk" drains as bytes are written.
        val data = ByteArray(1024 * 1024) { 7 }
        try {
            CacheCopier.copy({ data.inputStream() }, tmp, fin,
                { free - tmp.length().coerceAtLeast(0) }, 1L shl 33, 5_000_000)
            throw AssertionError("should have aborted")
        } catch (e: CacheCopier.NoSpace) { }
        assertFalse(tmp.exists()); assertFalse(fin.exists())
    }

    @Test fun concurrentCopiesSerializeAndReuseOneResult() {
        val d = dir()
        val lock = Object()
        val tmp = File(d, "x.part"); val fin = File(d, "x")
        val payloads = (1..8).map { ByteArray(64 * 1024) { it.toByte() } }
        val threads = (1..4).map {
            thread {
                synchronized(lock) {
                    if (!fin.exists())
                        CacheCopier.copy({ payloads[0].inputStream() }, tmp, fin, { 1L shl 40 }, 1L shl 33, 0)
                }
            }
        }
        threads.forEach { it.join() }
        assertTrue(fin.exists())
        assertFalse("no torn .part left behind", tmp.exists())
        assertEquals(64 * 1024, fin.length())
    }
}
