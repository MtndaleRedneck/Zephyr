package com.bingemote

/** Helper rediscovery policy (S11b, security-settled - the same rule as Zephyr's
 *  S11): the saved helper token is NEVER sent to a host learned from a broadcast.
 *  Any LAN device can answer the discovery magic, so a 200 to a token-carrying
 *  ping proves nothing - the ping itself already handed the token over, and every
 *  later VLC call would follow with the Basic-auth password. The plan type has no
 *  token-carrying variant at all: a spoofed responder structurally cannot receive
 *  the credential. Names are labels here too - no name-based exception. */
object HelperRediscover {
    // Retained for tests only: Plan, run() and plan() are the old notice-only API. Production uses findVerifiedMove through HelperMove.


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

    /** At most this many discovered candidates are handshake-checked per attempt. */
    const val MAX_CANDIDATES = 3

    /**
     * True only for a strictly formed IPv4 dotted quad or colon-hex IPv6 literal (optionally in brackets, no zone id)
     * that is private or link-local. Anything else, including every host name, is rejected before any address
     * conversion, so no name lookup can happen here.
     */
    fun isPrivateLiteral(host: String): Boolean {
        val a = parseLiteral(host) ?: return false
        // Java's isSiteLocalAddress misses IPv6 unique-local fc00::/7, which home routers actually hand out.
        val ula = a is java.net.Inet6Address && (a.address[0].toInt() and 0xFE) == 0xFC
        return a.isSiteLocalAddress || a.isLinkLocalAddress || ula
    }

    /** The address of a strictly formed IPv4 dotted quad or colon-hex IPv6 literal (optional brackets, no zone id); null for anything else. No name lookup. */
    fun parseLiteral(host: String): java.net.InetAddress? {
        val h = if (host.startsWith("[") && host.endsWith("]")) host.substring(1, host.length - 1) else host
        val v4 = Regex("(0|[1-9][0-9]{0,2})(\\.(0|[1-9][0-9]{0,2})){3}").matches(h) && h.split('.').all { it.toInt() in 0..255 }
        val v6 = h.contains(':') && h.length <= 39 && Regex("[0-9A-Fa-f:]+").matches(h) && !h.contains(":::") &&
            h.split(':').size <= 8 + 1 && h.split("::").size <= 2 && h.split(':').filter { it.isNotEmpty() }.all { it.length <= 4 }
        if (!v4 && !v6) return null
        return try { java.net.InetAddress.getByName(h) } catch (e: Exception) { null }
    }

    /** Same address and port compare equal whatever the textual form (IPv6 spellings, brackets). Non-literals fall back to the exact text. */
    fun addressKey(host: String, port: Int): String =
        (parseLiteral(host)?.address?.joinToString("") { "%02x".format(it) } ?: "name:$host") + ":" + port

    /**
     * Candidate for the remembered-PC move (NOT wired into any screen). Sends no application bytes and no token:
     * each acceptable discovered helper is handshake-checked against the key stored in [record], and only a match
     * yields a [SecureHttp.VerifiedAddress]. A responder with another key yields nothing and latches nothing, so an
     * impostor that copies the PC's name can neither be followed nor mark the real PC as changed.
     * Bounds: the address must differ from the saved one, be a private literal, with a valid port; duplicates are dropped;
     * candidates with the saved name come first; at most [MAX_CANDIDATES] are tried in total.
     */
    fun findVerifiedMove(
        savedHost: String, savedPort: Int, savedName: String, record: String,
        found: List<DiscoveredHelper>,
        addressOk: (String) -> Boolean = ::isPrivateLiteral,
        verify: (String, Int) -> SecureHttp.VerifiedAddress? = { h, p -> SecureHttp.verify(record, h, p) },
    ): SecureHttp.VerifiedAddress? {
        // The key proof, not the name, is the identity check, so a renamed PC must still be found: helpers carrying the saved
        // name are tried first, then the other private candidates, all inside the same cap.
        val savedKey = addressKey(savedHost, savedPort)
        val usable = found.asSequence()
            .filter { addressKey(it.host, it.port) != savedKey }
            .filter { it.port in 1..65535 && addressOk(it.host) }
            .distinctBy { addressKey(it.host, it.port) }
            .toList()
        val named = usable.filter { savedName.isNotBlank() && it.name.equals(savedName, ignoreCase = true) }
        val tried = (named + (usable - named.toSet())).take(MAX_CANDIDATES)
        for (c in tried) verify(c.host, c.port)?.let { return it }
        return null
    }
}
