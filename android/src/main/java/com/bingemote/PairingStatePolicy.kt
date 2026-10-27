package com.bingemote

/** N2 pairing-vs-outage classification (pure): a 401 BadToken means "pair again",
 *  NEVER an outage - and it clears any outage already showing. A good contact
 *  clears both. Ordinary failures count toward unreachable. One classifier for
 *  the poll path AND the queued-command path so the two can never drift. */
object PairingStatePolicy {

    data class Flags(val unpaired: Boolean, val unreachable: Boolean, val failures: Int)

    /** Any confirmed-good contact with the PC. */
    fun onSuccess(): Flags = Flags(unpaired = false, unreachable = false, failures = 0)

    /** The PC answered 401: the pairing is revoked. Not an outage - clears one. */
    fun onBadToken(): Flags = Flags(unpaired = true, unreachable = false, failures = 0)

    /** A network/other failure: the outage shows after unreachableAfter in a row. */
    fun onFailure(prev: Flags, unreachableAfter: Int): Flags {
        val f = prev.failures + 1
        return Flags(unpaired = prev.unpaired, unreachable = f >= unreachableAfter, failures = f)
    }
}
