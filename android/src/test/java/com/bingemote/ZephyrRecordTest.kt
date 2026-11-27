package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Two "stores" over one shared backing, like two Store instances over the same preferences, sharing one process-wide record. */
class ZephyrRecordTest {
    private class Backing { @Volatile var v: String? = null; var loads = 0 }

    @Test fun anotherInstanceSeesTheNewValueWithoutAReloadAndPeekMatches() {
        val rec = ZephyrRecord<String>(); val disk = Backing()
        fun readA() = rec.read { disk.loads++; disk.v }
        fun readB() = rec.read { disk.loads++; disk.v }
        disk.v = "old"
        assertEquals("old", readB())                        // instance B loaded before the re-pair
        rec.write { disk.v = "new"; "new" }                 // instance A re-pairs
        assertEquals("new", readB())                        // B is not stale
        assertEquals("new", rec.peek())                     // the pin reads the same record
        assertEquals("new", readA())
        assertEquals(1, disk.loads)                         // no second disk read
    }

    @Test fun aFailedWriteLeavesNothingPinnedAndTheNextReadReloads() {
        val rec = ZephyrRecord<String>(); val disk = Backing()
        disk.v = "old"; rec.read { disk.v }
        try { rec.write { disk.v = "half"; throw IllegalStateException("secure write failed") } } catch (e: IllegalStateException) {}
        assertNull(rec.peek())                              // pin refuses until reloaded
        assertEquals("half", rec.read { disk.v })           // reloads what is actually stored
        assertEquals("half", rec.peek())
    }

    @Test fun aFailingLoadIsNotCached() {
        val rec = ZephyrRecord<String>(); var fail = true
        try { rec.read { if (fail) throw IllegalStateException("unreadable") else "ok" } } catch (e: IllegalStateException) {}
        assertNull(rec.peek())
        fail = false
        assertEquals("ok", rec.read { if (fail) throw IllegalStateException() else "ok" })
    }

    @Test fun readersDuringAWriteNeverSeeAMixedValue() {
        val rec = ZephyrRecord<Pair<String, String>>()
        rec.write { "h1" to "t1" }
        val bad = java.util.concurrent.atomic.AtomicInteger()
        val stop = java.util.concurrent.atomic.AtomicBoolean()
        val readers = (1..3).map { Thread { while (!stop.get()) { val v = rec.peek(); if (v != null && v.first.substring(1) != v.second.substring(1)) bad.incrementAndGet() } }.also { it.start() } }
        for (i in 2..2000) rec.write { Thread.yield(); "h$i" to "t$i" }
        stop.set(true); readers.forEach { it.join() }
        assertEquals(0, bad.get())
        assertTrue(rec.peek() != null)
    }

    @Test fun peekStateSeparatesNoVerdictFromNothingPaired() {
        val rec = ZephyrRecord<String>()
        assertTrue(rec.peekState() is ZephyrRecord.Peek.Unavailable)          // never loaded
        rec.read { null }
        val unpaired = rec.peekState() as ZephyrRecord.Peek.Value<String>
        assertNull(unpaired.v)                                                // loaded, nothing paired: a verdict
        try { rec.write { throw IllegalStateException() } } catch (e: IllegalStateException) {}
        assertTrue(rec.peekState() is ZephyrRecord.Peek.Unavailable)          // failed write: no verdict
        rec.write { "x" }
        assertEquals("x", (rec.peekState() as ZephyrRecord.Peek.Value<String>).v)
    }
}
