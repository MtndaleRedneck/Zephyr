using System;
using System.Net.Http;
using System.Net.WebSockets;

namespace ZephyrVlcRemote.Tests;

/// <summary>
/// Real secure setup for the tests that start the real HttpServer on Program.HttpPort. The server is HTTPS only (it throws "Secure setup is not
/// complete" without the identity and both bindings), so these tests run the helper's own TlsSetup once per test process, and every client they use
/// is pinned to that identity's certificate (thumbprint from the setup marker): no plaintext, no "accept any certificate".
/// Ownership: TlsSetup refuses (non-zero) when a binding on the port is not provably ours, in which case the tests fail loudly and nothing is removed.
/// Cleanup calls TlsSetup.Remove, which removes only bindings the marker proves are ours; the certificate, key and marker stay (documented residue on a disposable runner).
/// Needs an elevated session (the CI runner is admin).
/// </summary>
internal static class HelperTestTls
{
    private static readonly object L = new();
    private static string? _thumb;
    private static bool _cleanupRegistered;

    internal static void EnsureReady()
    {
        lock (L)
        {
            if (_thumb != null) return;
            var rc = TlsSetup.Run(Program.HttpPort, true);
            if (rc != 0) throw new InvalidOperationException("secure test setup failed for port " + Program.HttpPort + " (TlsSetup exit " + rc + "; 6 = not elevated, 5 = planner refused, e.g. a foreign binding)");
            var (state, marker) = TlsMarkerStore.Read();
            if (state != MarkerState.Present || marker == null) throw new InvalidOperationException("secure test setup left no readable identity marker");
            _thumb = marker.Thumbprint;
            if (!_cleanupRegistered)
            {
                _cleanupRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { TlsSetup.Remove(Program.HttpPort); } catch { } };
            }
        }
    }

    private static bool Pinned(System.Security.Cryptography.X509Certificates.X509Certificate? c) =>
        c != null && _thumb != null && string.Equals(new System.Security.Cryptography.X509Certificates.X509Certificate2(c).Thumbprint, _thumb, StringComparison.OrdinalIgnoreCase);

    /// <summary>HttpClient for https://127.0.0.1:{HttpPort}: accepts only the setup identity's certificate.</summary>
    internal static HttpClient Client()
    {
        EnsureReady();
        var h = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => Pinned(cert) };
        return new HttpClient(h);
    }

    /// <summary>ClientWebSocket for wss://127.0.0.1:{HttpPort}/ws pinned to the same certificate.</summary>
    internal static ClientWebSocket Ws()
    {
        EnsureReady();
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => Pinned(cert);
        return ws;
    }
}
