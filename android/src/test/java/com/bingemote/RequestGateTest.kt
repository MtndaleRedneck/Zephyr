package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class RequestGateTest {
    private val pin = "ab".repeat(32)
    private val tok = "a1".repeat(32)
    private fun cred(id: String, host: String = "10.0.0.5", token: String = tok) = PairedCredential.of(id, pin, host, 8492, token)!!
    private val a = cred("0".repeat(32))
    private val b = cred("f".repeat(32), host = "10.0.0.9", token = "c3".repeat(32))

    @Test fun supplierIsReadExactlyOnceSoAddressPinAndTokenCannotMix() {
        KeyChangeState.resetForTests()
        var reads = 0
        val src = { reads++; if (reads == 1) a else b }          // a re-pair lands after the first read
        val d = RequestGate.decide(src, a.host, a.port, null) as RequestGate.Decision.Allow
        assertEquals(1, reads)
        assertEquals(a.token, d.credential.token)
        assertEquals(a.host, d.credential.host)
    }

    @Test fun callerHoldingAnotherPcIsRefusedWithNothingSent() {
        KeyChangeState.resetForTests()
        assertTrue(RequestGate.decide({ b }, a.host, a.port, null) is RequestGate.Decision.NotPaired)   // re-pair between caller reads
        assertTrue(RequestGate.decide({ null }, a.host, a.port, null) is RequestGate.Decision.NotPaired) // forget
    }

    @Test fun afterAMismatchNoFurtherRequestIsAllowedUntilANewIdentityExists() {
        KeyChangeState.resetForTests()
        KeyChangeState.mark(a.pcId)
        assertTrue(RequestGate.decide({ a }, a.host, a.port, null) is RequestGate.Decision.KeyChanged)
        assertTrue(RequestGate.decide({ a }, null, null, null) is RequestGate.Decision.KeyChanged)
        assertTrue(RequestGate.decide({ b }, b.host, b.port, null) is RequestGate.Decision.Allow)       // explicit re-pair = new identity
        KeyChangeState.resetForTests()
    }

    @Test fun websocketReconnectStopsAfterMismatchForgetPauseAndShutdown() {
        KeyChangeState.resetForTests()
        assertTrue(RequestGate.mayReconnect(false, false, false) { a })
        assertFalse(RequestGate.mayReconnect(true, false, false) { a })
        assertFalse(RequestGate.mayReconnect(false, true, false) { a })
        assertFalse(RequestGate.mayReconnect(false, false, true) { a })
        assertFalse(RequestGate.mayReconnect(false, false, false) { null })
        KeyChangeState.mark(a.pcId)
        assertFalse(RequestGate.mayReconnect(false, false, false) { a })
        KeyChangeState.resetForTests()
    }

    @Test fun sameAddressRePairWithAnotherTokenIsRefusedAndNothingIsSent() {
        KeyChangeState.resetForTests()
        val rePaired = cred("e".repeat(32), host = a.host, token = "d4".repeat(32))   // same address, new pairing
        assertTrue(RequestGate.decide({ rePaired }, a.host, a.port, a.token) is RequestGate.Decision.NotPaired)
        assertTrue(RequestGate.decide({ null }, a.host, a.port, a.token) is RequestGate.Decision.NotPaired)
    }

    @Test fun matchingTokenIsAllowedAndCurrentRecordCallerStillWorks() {
        KeyChangeState.resetForTests()
        val d = RequestGate.decide({ a }, a.host, a.port, a.token) as RequestGate.Decision.Allow
        assertEquals(a.token, d.credential.token)
        assertTrue(RequestGate.decide({ a }, null, null, null) is RequestGate.Decision.Allow)   // websocket: current record
    }

    @Test fun keyChangeStillStopsAMatchingToken() {
        KeyChangeState.resetForTests()
        KeyChangeState.mark(a.pcId)
        assertTrue(RequestGate.decide({ a }, a.host, a.port, a.token) is RequestGate.Decision.KeyChanged)
    }
}
