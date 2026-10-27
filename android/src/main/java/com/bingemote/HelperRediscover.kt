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

    /** Executes the safe rediscovery against injected network calls - the SAME
     *  entry point RemoteState uses. The token-carrying ping exists only as
     *  pingSaved and is invoked only for the saved endpoint; discovered hosts
     *  are never probed, and there is no save-host callback anywhere in this
     *  API, so rediscovery structurally cannot leak a credential or mutate the
     *  saved pairing. */
    fun run(
        savedHost: String, savedPort: Int, savedName: String = "",
        pingSaved: () -> Boolean,
        discover: () -> List<DiscoveredHelper>,
    ): Plan {
        if (pingSaved()) return Plan.SavedEndpointSeen
        return plan(savedHost, savedPort, savedName, discover())
    }

    fun plan(savedHost: String, savedPort: Int, savedName: String = "", found: List<DiscoveredHelper>): Plan {
        for (h in found) {
            if (h.host == savedHost && h.port == savedPort) return Plan.SavedEndpointSeen
        }
        // S-6: only a responder with the SAVED helper's name can be "the helper
        // moved" - any other PC running the helper is just another helper.
        if (savedName.isBlank()) return Plan.None
        val first = found.firstOrNull { it.name.equals(savedName, ignoreCase = true) } ?: return Plan.None
        return Plan.MovedNotice(first.host, first.port, first.name)
    }
}
