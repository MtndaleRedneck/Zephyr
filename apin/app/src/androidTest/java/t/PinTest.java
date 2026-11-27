package t;

import android.util.Log;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import javax.net.ssl.*;
import java.net.*;
import java.security.MessageDigest;
import java.security.cert.X509Certificate;
import java.util.concurrent.TimeUnit;

public class PinTest {
    static final String TAG = "PINT";
    static final String HOST = "10.0.2.2";
    static final int PORT = 48443;

    static String hex(byte[] b) { StringBuilder s = new StringBuilder(); for (byte x : b) s.append(String.format("%02x", x)); return s.toString(); }

    static class PinTm implements X509TrustManager {
        final String pin;
        PinTm(String p) { pin = p; }
        public void checkClientTrusted(X509Certificate[] c, String a) throws java.security.cert.CertificateException { throw new java.security.cert.CertificateException("no client auth"); }
        public void checkServerTrusted(X509Certificate[] c, String a) throws java.security.cert.CertificateException {
            try {
                String got = hex(MessageDigest.getInstance("SHA-256").digest(c[0].getPublicKey().getEncoded()));
                Log.i(TAG, "  presented leaf: notBefore=" + c[0].getNotBefore() + " notAfter=" + c[0].getNotAfter() + " now=" + new java.util.Date() + " spkiSha256=" + got + " configuredPin=" + pin + " match=" + got.equals(pin));
                if (!got.equals(pin)) throw new java.security.cert.CertificateException("pin mismatch");
            } catch (java.security.NoSuchAlgorithmException e) { throw new RuntimeException(e); }
        }
        public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
    }

    static SSLSocketFactory factory(X509TrustManager tm) throws Exception {
        SSLContext ctx = SSLContext.getInstance("TLS");
        ctx.init(null, new TrustManager[] { tm }, null);
        return ctx.getSocketFactory();
    }

    static String urlGet(String url, SSLSocketFactory f, boolean verifierOff) {
        try {
            HttpsURLConnection c = (HttpsURLConnection) new URL(url).openConnection();
            if (f != null) c.setSSLSocketFactory(f);
            if (verifierOff) c.setHostnameVerifier((h, s) -> true);
            c.setConnectTimeout(8000); c.setReadTimeout(8000);
            return "status=" + c.getResponseCode();
        } catch (Exception e) { return "FAILED " + e.getClass().getSimpleName() + ": " + e.getMessage(); }
    }

    @Test
    public void matrix() throws Exception {
        String pin = InstrumentationRegistry.getArguments().getString("pin");
        String wrong = "0000000000000000000000000000000000000000000000000000000000000000";
        String base = "https://" + HOST + ":" + PORT + "/";
        log("android sdk " + android.os.Build.VERSION.SDK_INT + " pin " + pin);
        log("1 default trust: " + urlGet(base, null, false));
        log("2 pin TM + default hostname verifier: " + urlGet(base, factory(new PinTm(pin)), false));
        log("3 pin TM + scoped verifier off (HttpsURLConnection): " + urlGet(base, factory(new PinTm(pin)), true));
        log("4 WRONG pin + verifier off: " + urlGet(base, factory(new PinTm(wrong)), true));
        try (SSLSocket s = (SSLSocket) factory(new PinTm(pin)).createSocket(HOST, PORT)) { s.setSoTimeout(5000); s.startHandshake(); log("5 raw SSLSocket right pin: OK " + s.getSession().getProtocol()); }
        catch (Exception e) { log("5 raw SSLSocket right pin: FAILED " + e); }
        try (SSLSocket s = (SSLSocket) factory(new PinTm(wrong)).createSocket(HOST, PORT)) { s.startHandshake(); log("6 raw SSLSocket wrong pin: handshake OK (BAD)"); }
        catch (Exception e) { log("6 raw SSLSocket wrong pin: rejected " + e.getClass().getSimpleName()); }
        try {
            PinTm tm = new PinTm(pin);
            OkHttpClient ok = new OkHttpClient.Builder().sslSocketFactory(factory(tm), tm).hostnameVerifier((h, s) -> true).callTimeout(10, TimeUnit.SECONDS).build();
            okhttp3.Response r = ok.newCall(new Request.Builder().url(base).build()).execute();
            log("7 OkHttp pin TM + verifier off: status=" + r.code());
        } catch (Exception e) { log("7 OkHttp pin TM + verifier off: FAILED " + e); }
        try {
            PinTm tm = new PinTm(pin);
            OkHttpClient ok = new OkHttpClient.Builder().sslSocketFactory(factory(tm), tm).callTimeout(10, TimeUnit.SECONDS).build();
            ok.newCall(new Request.Builder().url(base).build()).execute();
            log("8 OkHttp pin TM + DEFAULT verifier: status OK (unexpected)");
        } catch (Exception e) { log("8 OkHttp pin TM + DEFAULT verifier: FAILED " + e.getClass().getSimpleName() + ": " + e.getMessage()); }
        try {
            HttpURLConnection c = (HttpURLConnection) new URL("http://" + HOST + ":" + PORT + "/").openConnection();
            c.setConnectTimeout(5000); c.setReadTimeout(5000);
            log("9 cleartext http (usesCleartextTraffic=false): status=" + c.getResponseCode() + " (BAD if status)");
        } catch (Exception e) { log("9 cleartext http (usesCleartextTraffic=false): blocked " + e.getClass().getSimpleName() + ": " + e.getMessage()); }
    }

    static String okDefault(String url) {
        try { okhttp3.Response r = new OkHttpClient.Builder().callTimeout(10, TimeUnit.SECONDS).build().newCall(new Request.Builder().url(url).build()).execute(); return "status=" + r.code() + " (BAD)"; }
        catch (Exception e) { return "rejected " + e.getClass().getSimpleName(); }
    }

    @Test
    public void renewal() throws Exception {
        String pin = InstrumentationRegistry.getArguments().getString("pin");
        String base2 = "https://" + HOST + ":48444/";
        String base1 = "https://" + HOST + ":" + PORT + "/";
        log("R0 sdk " + android.os.Build.VERSION.SDK_INT + " old-cert pin " + pin);
        log("R1 same-key RENEWED cert (new dates, same SPKI), old pin, scoped verifier: " + urlGet(base2, factory(new PinTm(pin)), true));
        log("R2 old expired cert, old pin: " + urlGet(base1, factory(new PinTm(pin)), true));
        log("R3 default HttpsURLConnection AFTER pinned use, expired port: " + urlGet(base1, null, false));
        log("R4 default HttpsURLConnection AFTER pinned use, renewed port: " + urlGet(base2, null, false));
        log("R5 default OkHttp client AFTER pinned use, expired port: " + okDefault(base1));
        log("R6 default OkHttp client AFTER pinned use, renewed port: " + okDefault(base2));
        log("R7 default HttpsURLConnection to a normal public site (isolation sanity): " + urlGet("https://www.google.com/", null, false));
        log("R8 wrong pin on renewed cert: " + urlGet(base2, factory(new PinTm("0000000000000000000000000000000000000000000000000000000000000000")), true));
    }

    static void log(String s) { Log.i(TAG, s); }
}
