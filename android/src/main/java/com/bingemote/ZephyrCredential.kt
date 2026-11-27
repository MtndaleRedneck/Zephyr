package com.bingemote

/**
 * The trusted credential of a Zephyr Player pairing as ONE stored value: the phone's own id for the Player, the Player's key hash,
 * its address and the pairing token. Its own version tag ("z1") means a Zephyr record can never parse as the helper's ("v3") or the
 * reverse. The pcId is generated on the phone for each pairing and is never the helper's id. Field rules are exactly [PairedCredential.of].
 */
class ZephyrCredential private constructor(val pcId: String, val pinHex: String, val host: String, val port: Int, val token: String) {
    fun encode(): String = "z1|$pcId|$pinHex|$host|$port|$token"

    companion object {
        fun of(pcId: String, pinHex: String, host: String, port: Int, token: String): ZephyrCredential? =
            PairedCredential.of(pcId, pinHex, host, port, token)?.let { ZephyrCredential(pcId, pinHex, host, port, token) }

        fun decode(raw: String): ZephyrCredential? {
            val p = raw.split('|')
            return if (p.size == 6 && p[0] == "z1") p[4].toIntOrNull()?.let { of(p[1], p[2], p[3], it, p[5]) } else null
        }
    }

    /** What the phone may do with what is stored. Only [Secure] can connect. */
    sealed class State {
        object Unpaired : State()
        class Secure(val credential: ZephyrCredential) : State()
        /** Only an old plain-HTTP pairing exists: unusable and never read as a fallback; pair again. Nothing is deleted. */
        object NeedsRepair : State()
        /** A save was interrupted, the record does not parse, or the store could not be read: unusable, never reads as absent. */
        object Blocked : State()
    }

    object Resolver {
        fun resolve(credentialRaw: String, intentRaw: String, legacyPresent: Boolean, storeBroken: Boolean): State = when {
            storeBroken -> State.Blocked
            intentRaw.isNotEmpty() -> State.Blocked
            credentialRaw.isNotEmpty() -> decode(credentialRaw)?.let { State.Secure(it) } ?: State.Blocked
            legacyPresent -> State.NeedsRepair
            else -> State.Unpaired
        }
    }
}
