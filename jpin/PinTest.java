import javax.net.ssl.*;
import java.io.*;
import java.net.*;
import java.nio.file.*;
import java.security.*;
import java.security.cert.X509Certificate;
import java.util.*;

public class PinTest {
    static String hex(byte[] b) { StringBuilder s = new StringBuilder(); for (byte x : b) s.append(String.format("%02x", x)); return s.toString(); }

    static class PinTm implements X509TrustManager {
        final String pin;
        PinTm(String p) { pin = p; }
        public void checkClientTrusted(X509Certificate[] c, String a) throws java.security.cert.CertificateException { throw new java.security.cert.CertificateException("no client auth"); }
        public void checkServerTrusted(X509Certificate[] c, String a) throws java.security.cert.CertificateException {
            try {
                String got = hex(MessageDigest.getInstance("SHA-256").digest(c[0].getPublicKey().getEncoded()));
                if (!got.equals(pin)) throw new java.security.cert.CertificateException("pin mismatch " + got);
            } catch (NoSuchAlgorithmException e) { throw new RuntimeException(e); }
        }
        public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
    }

    static SSLSocketFactory factory(X509TrustManager tm) throws Exception {
        SSLContext ctx = SSLContext.getInstance("TLS");
        ctx.init(null, new TrustManager[] { tm }, null);
        return ctx.getSocketFactory();
    }

    static String get(String url, SSLSocketFactory f, boolean allowAllHosts) {
        try {
            HttpsURLConnection c = (HttpsURLConnection) new URL(url).openConnection();
            if (f != null) c.setSSLSocketFactory(f);
            if (allowAllHosts) c.setHostnameVerifier((h, s) -> true);
            c.setConnectTimeout(5000); c.setReadTimeout(5000);
            int code = c.getResponseCode();
            return "status=" + code;
        } catch (Exception e) { return "FAILED " + e.getClass().getSimpleName() + ": " + e.getMessage(); }
    }

    public static void main(String[] a) throws Exception {
        String ks = a[0], pw = a[1], pin = a[2];
        int port = Integer.parseInt(a[3]);
        KeyStore k = KeyStore.getInstance("PKCS12");
        try (InputStream in = new FileInputStream(ks)) { k.load(in, pw.toCharArray()); }
        KeyManagerFactory kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        kmf.init(k, pw.toCharArray());
        SSLContext sctx = SSLContext.getInstance("TLS");
        sctx.init(kmf.getKeyManagers(), null, null);
        SSLServerSocket ss = (SSLServerSocket) sctx.getServerSocketFactory().createServerSocket(port);
        Thread t = new Thread(() -> {
            while (true) {
                try (Socket s = ss.accept()) {
                    s.setSoTimeout(3000);
                    BufferedReader r = new BufferedReader(new InputStreamReader(s.getInputStream()));
                    while (true) { String l = r.readLine(); if (l == null || l.isEmpty()) break; }
                    OutputStream o = s.getOutputStream();
                    o.write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok".getBytes());
                    o.flush();
                } catch (Exception e) { }
            }
        });
        t.setDaemon(true); t.start();
        if (a.length > 4 && a[4].equals("serve")) { System.out.println("serving"); Thread.sleep(900000); return; }
        String base = "https://127.0.0.1:" + port + "/";
        String wrongHost = "https://localhost:" + port + "/";
        String wrongPin = "0".repeat(64);
        System.out.println("cert: expired and with a different SAN than the address used (see workflow log for keytool output)");
        System.out.println("1 default platform trust, no pin: " + get(base, null, false));
        System.out.println("2 pin-only TrustManager, right pin, default hostname verifier (IP URL): " + get(base, factory(new PinTm(pin)), false));
        System.out.println("3 pin-only TrustManager, right pin, default hostname verifier (localhost URL): " + get(wrongHost, factory(new PinTm(pin)), false));
        System.out.println("4 pin-only TrustManager, right pin, hostname verifier disabled (IP URL): " + get(base, factory(new PinTm(pin)), true));
        System.out.println("5 pin-only TrustManager, WRONG pin, hostname verifier disabled: " + get(base, factory(new PinTm(wrongPin)), true));
        // raw SSLSocket: no hostname verification unless requested
        try (SSLSocket s = (SSLSocket) factory(new PinTm(pin)).createSocket("127.0.0.1", port)) {
            s.setSoTimeout(4000);
            s.startHandshake();
            System.out.println("6 raw SSLSocket pin-only, right pin: handshake OK, protocol " + s.getSession().getProtocol());
        } catch (Exception e) { System.out.println("6 raw SSLSocket FAILED " + e); }
        try (SSLSocket s = (SSLSocket) factory(new PinTm(wrongPin)).createSocket("127.0.0.1", port)) {
            s.startHandshake();
            System.out.println("7 raw SSLSocket WRONG pin: handshake OK (BAD)");
        } catch (Exception e) { System.out.println("7 raw SSLSocket wrong pin: rejected " + e.getClass().getSimpleName()); }
        // cleartext HTTP to a TLS port
        try {
            HttpURLConnection c = (HttpURLConnection) new URL("http://127.0.0.1:" + port + "/").openConnection();
            c.setConnectTimeout(4000); c.setReadTimeout(4000);
            System.out.println("8 cleartext http to TLS port: status=" + c.getResponseCode() + " (BAD if 200)");
        } catch (Exception e) { System.out.println("8 cleartext http to TLS port: FAILED " + e.getClass().getSimpleName()); }
        System.exit(0);
    }
}
