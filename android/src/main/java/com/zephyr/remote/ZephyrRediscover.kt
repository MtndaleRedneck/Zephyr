package com.zephyr.remote

/** Re-discovery policy (S11, security-settled): the saved bearer token is NEVER
 *  sent to a discovered host. Any device can answer a broadcast - a matching
 *  name proves nothing, so no name-based exception exists. The plan type has no
 *  "probe with token" variant at all: a spoofed same-name candidate structurally
 *  cannot receive the credential. */
object ZephyrRediscover {

    sealed interface Plan {
        /** The saved endpoint answered discovery: the normal poll recovers by itself. */
        object SavedEndpointSeen : Plan
        /** A same-name PC appeared at a NEW address: tell the user; re-pair is the
         *  only safe way to use it (a fresh code proves the PC, never the token). */
        data class MovedNotice(val host: String, val port: Int, val name: String) : Plan
        /** Nothing trustworthy found: keep the saved endpoint, keep polling. */
        object None : Plan
    }

    /** The entry point RemoteState uses. Discovery results only ever produce a
     *  Plan - this API takes no token and no HTTP client, so a spoofed responder
     *  cannot be probed and the saved endpoint cannot be overwritten from here. */
    fun run(
        savedHost: String, savedPort: Int, savedName: String,
        discover: () -> List<com.bingemote.DiscoveredHelper>,
    ): Plan = plan(savedHost, savedPort, savedName, discover())

    fun plan(
        savedHost: String, savedPort: Int, savedName: String,
        found: List<com.bingemote.DiscoveredHelper>,
    ): Plan {
        for (h in found) {
            if (h.host == savedHost && h.port == savedPort) return Plan.SavedEndpointSeen
        }
        if (savedName.isBlank()) return Plan.None
        val named = found.filter { it.name.equals(savedName, ignoreCase = true) }
        // Even one same-name candidate only earns a NOTICE - never the token.
        val first = named.firstOrNull() ?: return Plan.None
        return Plan.MovedNotice(first.host, first.port, first.name)
    }
}
