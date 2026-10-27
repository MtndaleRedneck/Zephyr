package com.bingemote

/** Helper rediscovery policy (S11b, security-settled - the same rule as Zephyr's
 *  S11): the saved helper token is NEVER sent to a host learned from a broadcast.
 *  Any LAN device can answer the discovery magic, so a 200 to a token-carrying
 *  ping proves nothing - the ping itself already handed the token over, and every
 *  later VLC call would follow with the Basic-auth password. The plan type has no
 *  token-carrying variant at all: a spoofed responder structurally cannot receive
 *  the credential. Names are labels here too - no name-based exception. */
object HelperRediscover {

    sealed interface Plan {
        /** The saved endpoint answered discovery: the normal poll recovers by itself. */
        object SavedEndpointSeen : Plan
        /** A helper appeared at a NEW address: tell the user - explicit re-pair
         *  (a fresh code shown by the helper window) is the only safe way in. */
        data class MovedNotice(val host: String, val port: Int, val name: String) : Plan
        /** Nothing found: keep the saved endpoint, keep polling. */
        object None : Plan
    }

    fun plan(savedHost: String, savedPort: Int, found: List<DiscoveredHelper>): Plan {
        for (h in found) {
            if (h.host == savedHost && h.port == savedPort) return Plan.SavedEndpointSeen
        }
        val first = found.firstOrNull() ?: return Plan.None
        return Plan.MovedNotice(first.host, first.port, first.name)
    }
}
