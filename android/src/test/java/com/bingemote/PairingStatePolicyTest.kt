package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** N2 regression: a re-paired phone must not keep showing "pair again", and a
 *  401 (BadToken) is never "Can't reach your PC". */
class PairingStatePolicyTest {

    @Test fun `a good poll clears pair-again and any outage`() {
        val f = PairingStatePolicy.onSuccess()
        assertFalse(f.unpaired); assertFalse(f.unreachable); assertEquals(0, f.failures)
    }

    @Test fun `BadToken sets pair-again and clears the outage`() {
        val f = PairingStatePolicy.onBadToken()
        assertTrue(f.unpaired); assertFalse(f.unreachable); assertEquals(0, f.failures)
    }

    @Test fun `a 401 during taps does not become can't-reach after three taps`() {
        // The exact N2 report: taps after the PC removed the phone showed an outage.
        var f = PairingStatePolicy.Flags(unpaired = false, unreachable = false, failures = 0)
        repeat(3) { f = PairingStatePolicy.onBadToken() }
        assertTrue(f.unpaired); assertFalse(f.unreachable)
    }

    @Test fun `ordinary failures count to the outage threshold and keep pair-again`() {
        var f = PairingStatePolicy.Flags(unpaired = true, unreachable = false, failures = 0)
        f = PairingStatePolicy.onFailure(f, 2)
        assertFalse(f.unreachable); assertTrue(f.unpaired) // a blip keeps the message
        f = PairingStatePolicy.onFailure(f, 2)
        assertTrue(f.unreachable); assertTrue(f.unpaired)  // threshold reached, pairing state kept
    }
}
