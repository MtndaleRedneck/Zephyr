package com.bingemote

import okhttp3.mockwebserver.MockWebServer
import java.io.ByteArrayInputStream
import java.security.KeyStore
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext

/** A fake Zephyr Player that speaks TLS, for instrumented tests. The key is a throwaway test key with no use anywhere else
 *  (self-signed, EC P-256); it exists so the phone's pinned-https client can be exercised against a fake host. */
object TlsFakePlayer {
    private const val P12 =
        "MIIDLgIBAzCCAucGCSqGSIb3DQEHAaCCAtgEggLUMIIC0DCB1QYJKoZIhvcNAQcBoIHHBIHEMIHBMIG+BgsqhkiG9w0BDAoBAqB3" +
        "MHUwKQYKKoZIhvcNAQwBAzAbBBTLv7jdOBmJ/JBVSPBcxiP3OfCmwQIDAMNQBEhX2rOn5lFKE6tc4whK0TMLb7E6HyUyh084MZml" +
        "7o9I7rmMObz7fBjfir0lIdEsx0hDXiXYXasF+azuuTNDmcMnsl6l2U+ApKYxNjARBgkqhkiG9w0BCRQxBB4CAHMwIQYJKoZIhvcN" +
        "AQkVMRQEElRpbWUgMTc5NTc5MzEwOTU0MTCCAfQGCSqGSIb3DQEHBqCCAeUwggHhAgEAMIIB2gYJKoZIhvcNAQcBMCkGCiqGSIb3" +
        "DQEMAQYwGwQUpSXKWJCw+BMuj3kBUO+0cAeGKPECAwDDUICCAaBRa3FBMUZOn67OPyWqGb1Zb7ZLMLj9ZpmESnLRKaCnrtq0e2IJ" +
        "OO0ITQlLHk4PuR/9Ce8tUNSMRq5BDKGLfNoqF/gK2bwDuUxNU7BPJHX0tZZUJgFFHeK+WU/OI+wmwn1BgZMG4K/TjZIywiT/ofjF" +
        "iQLZ5hYhxaWyZfnJNQCg4qfFDx36yhuGTwM7aGO7X7/DOtvleWWJIFJD58nJkuFo7gEpRL2luDYE4gxI1QbTxvtOfhAbvS75+VGU" +
        "zCEn4CimUV44MAvzDwTBksKbe1X+yotF7V0nsWLKx5SYiYHgUmb1UGsJRIOzKJVOvgzS/it7/Vu4IbU4rpngmDFS9qA+S07HKoj/" +
        "E1JKS8+kOpZhaugxEJ1luoPxB/2ZqK8POzSo9pFIQZl0wEmqnoc7qZE86Z00lXypFWE0NXq300L8dX+ZCXVpOC/nuQzwx6wnbQ9r" +
        "BNV+lA6/ySB5WECNbfKhtCa0qhnHL8MtNePoBSC96P2MZmZPLueUnPJnl2SONuLOqonfbJirHniBIO6ggK+dUpV0bV7ReJCMSb9Y" +
        "8dOF5TA+MCEwCQYFKw4DAhoFAAQUQd9RdExLUnDDt0BA6eQxY5RHV+gEFIzmsp/2KaQMDxn0T0U1UZN1HSk7AgMBhqA="

    private val store: KeyStore by lazy {
        KeyStore.getInstance("PKCS12").apply { load(ByteArrayInputStream(android.util.Base64.decode(P12, android.util.Base64.DEFAULT)), "changeit".toCharArray()) }
    }
    val sslContext: SSLContext by lazy {
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, "changeit".toCharArray()) }
        SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, null, null) }
    }
    /** Hex of the pin the phone derives for this fake's key. */
    val pinHex: String by lazy { PairedCredential.toHex(PinPolicy.hashSpki(store.getCertificate("s").publicKey.encoded)) }

    /** A started HTTPS MockWebServer presenting this key (HTTP/1.1 only, so the fake behaves like a minimal Player). */
    fun server(): MockWebServer = MockWebServer().apply {
        useHttps(sslContext.socketFactory, false)
        protocols = listOf(okhttp3.Protocol.HTTP_1_1)
        start()
    }
}
