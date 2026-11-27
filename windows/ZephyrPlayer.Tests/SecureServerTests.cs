using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using SecureLan;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>Real http.sys HTTPS against the real PlaybackServer, with a phone-side client that applies
    /// SecureLan.PinStore to the certificate it actually received on the connection. Needs an elevated Windows
    /// session (certificate store + sslcert binding); CI runners are elevated. This is a Windows .NET client, not Android.</summary>
    public class SecureServerTests
    {
        private static bool IsAdmin()
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        private static string Netsh(string args)
        {
            var psi = new ProcessStartInfo("netsh", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); return o;
        }
        private static X509Certificate2 NewCert(ECDsa key)
        {
            var req = new CertificateRequest("CN=zephyr-test-pc", key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            return new X509Certificate2(c.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
        }
        private sealed class Bound : IDisposable
        {
            public int Port; public X509Certificate2 Cert = null!; private X509Store? _st;
            public static Bound Make(int port, ECDsa key)
            {
                var b = new Bound { Port = port, Cert = NewCert(key) };
                b._st = new X509Store(StoreName.My, StoreLocation.LocalMachine); b._st.Open(OpenFlags.ReadWrite); b._st.Add(b.Cert);
                var o = Netsh($"http add sslcert ipport=0.0.0.0:{port} certhash={b.Cert.Thumbprint} appid={{12345678-1234-1234-1234-123456789abc}}");
                Assert.Contains("successfully", o, StringComparison.OrdinalIgnoreCase);
                return b;
            }
            public void Dispose() { Netsh($"http delete sslcert ipport=0.0.0.0:{Port}"); try { _st?.Remove(Cert); _st?.Close(); } catch { } }
        }

        private sealed class PinHandler : IDisposable
        {
            public readonly PinStore Pins; public readonly bool Enroll; public byte[]? Seen; public PinCheck? Last;
            public readonly HttpClient Client;
            public PinHandler(PinStore pins, bool enroll)
            {
                Pins = pins; Enroll = enroll;
                var h = new HttpClientHandler();
                h.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
                {
                    if (cert == null) return false;
                    var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();   // the leaf actually received on THIS connection
                    Last = Pins.Check("pc-1", spki, Enroll, out var hash); Seen = hash;
                    return Last != PinCheck.Mismatch;
                };
                Client = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(10) };
            }
            public void Dispose() => Client.Dispose();
        }

        private static async Task<HttpResponseMessage> Post(HttpClient c, string url, string json, string? bearer = null)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (bearer != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return await c.SendAsync(r);
        }

        [Fact]
        public async Task PairOverTls_PinAndTokenCommittedTogether_ThenMismatchHardFails_NoSecretSent()
        {
            if (!IsAdmin()) { Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "CI must be elevated"); return; }
            const int port = 47891;
            using var k1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var good = Bound.Make(port, k1);
            int tokenChecks = 0;
            using var server = new PlaybackServer((u, p, s) => Task.CompletedTask, t => { tokenChecks++; return t == "tok-1"; }, port, loopbackOnly: true,
                onPair: (code, source) => code == "1234" ? (PairOutcome.Ok, "tok-1") : (PairOutcome.BadCode, null), secure: true);
            server.Start();
            var url = $"https://127.0.0.1:{port}";

            // 1) explicit pairing: enrolls the key seen on this connection; commit only after the token arrives.
            var pins = new PinStore();
            using (var pair = new PinHandler(pins, enroll: true))
            {
                var bad = await Post(pair.Client, url + "/pair", "{\"code\":\"0000\"}");
                Assert.NotEqual(System.Net.HttpStatusCode.OK, bad.StatusCode);
                Assert.False(pins.Has("pc-1"));                       // a failed pairing enrolls nothing
                var ok = await Post(pair.Client, url + "/pair", "{\"code\":\"1234\"}");
                Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
                Assert.Contains("tok-1", await ok.Content.ReadAsStringAsync());
                Assert.True(pins.Commit("pc-1", pair.Seen!));            // pin + token committed together by the caller
            }
            Assert.Equal(PinStore.HashSpki(good.Cert.PublicKey.ExportSubjectPublicKeyInfo()).Length, 32);

            // 2) later connection, same key: pin matches, token accepted.
            using (var later = new PinHandler(pins, enroll: false))
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url + "/library/list"); r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok-1");
                var resp = await later.Client.SendAsync(r);
                Assert.Equal(PinCheck.Match, later.Last);
                Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
            }

            // 3) plaintext to the TLS port: no response served, token never checked.
            int before = tokenChecks;
            using (var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var r = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/library/list"); r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok-1");
                try { var resp = await plain.SendAsync(r); Assert.NotEqual(System.Net.HttpStatusCode.OK, resp.StatusCode); } catch (HttpRequestException) { }
            }
            Assert.Equal(before, tokenChecks);

            // 4) different key on the same address: hard fail before any request (so no token is ever sent).
            server.Dispose();
            Netsh($"http delete sslcert ipport=0.0.0.0:{port}");
            using var k2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var imposter = Bound.Make(port, k2);
            int tokenChecks2 = 0;
            using var server2 = new PlaybackServer((u, p, s) => Task.CompletedTask, t => { tokenChecks2++; return true; }, port, loopbackOnly: true, secure: true);
            server2.Start();
            using (var again = new PinHandler(pins, enroll: false))
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url + "/library/list"); r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok-1");
                await Assert.ThrowsAsync<HttpRequestException>(() => again.Client.SendAsync(r));
                Assert.Equal(PinCheck.Mismatch, again.Last);
                Assert.Equal(0, tokenChecks2);                           // the secret never reached the imposter
            }
            // and an enroll flag can never replace an existing pin
            using (var sneaky = new PinHandler(pins, enroll: true))
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => Post(sneaky.Client, url + "/pair", "{\"code\":\"1234\"}"));
                Assert.Equal(PinCheck.Mismatch, sneaky.Last);
            }
        }
    }
}
