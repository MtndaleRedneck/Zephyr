using System;
using System.IO;
using System.Net;
using System.Security.Principal;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>
/// Real Windows checks of the secure identity lifecycle: elevated setup (certificate store, http.sys sslcert binding),
/// unelevated runtime readiness, a real loopback handshake, idempotent re-run, and ownership-checked removal.
/// They change machine state (LocalMachine\MY, http.sys, ProgramData), so they run ONLY when ZEPHYR_TLS_INTEGRATION=1
/// on a disposable machine (a CI runner), on a private test port, never the shipped one. Otherwise each test returns early.
/// </summary>
[Collection("ProgramState")]
public class HelperTlsWindowsIntegrationTests
{
    private const int TestPort = 47993;
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("ZEPHYR_TLS_INTEGRATION") == "1";
    private static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static void BothBindings(HttpSysSsl.Kind expect)
    {
        Assert.Equal(expect, HttpSysSsl.QueryExact_(IPAddress.Any, TestPort).Kind);
        if (Socket_V6()) Assert.Equal(expect, HttpSysSsl.QueryExact_(IPAddress.IPv6Any, TestPort).Kind);
    }
    private static bool Socket_V6() => TlsPolicy.V6Required(System.Net.Sockets.Socket.OSSupportsIPv6);

    [Fact]
    public void ElevatedSetupBindsReadiesAndHandshakesThenRemoves()
    {
        if (!Enabled) return;
        Assert.True(IsAdmin, "this test needs an elevated runner");
        Assert.Equal(0, TlsSetup.Run(TestPort, true));
        var (ms, marker) = TlsMarkerStore.Read();
        Assert.Equal(MarkerState.Present, ms);
        Assert.NotNull(marker);
        Assert.Equal(CertState.Present, TlsInspector.FindByThumbprint(marker!.Thumbprint, privilegedKeyCheck: true).state);   // real signature with the key
        BothBindings(HttpSysSsl.Kind.Found);
        Assert.True(HelperTls.RuntimeReady(TestPort, out var why), why);

        using (var l = new HttpListener())
        {
            l.Prefixes.Add($"https://+:{TestPort}/");
            l.Start();
            Assert.True(HelperTls.VerifyListener(TestPort, out var hs), hs);   // real handshake presents the marker's key
            l.Stop();
        }

        // Re-run is a no-op: same identity, same thumbprint.
        Assert.Equal(0, TlsSetup.Run(TestPort, true));
        Assert.Equal(marker.Thumbprint, TlsMarkerStore.Read().marker!.Thumbprint);
        Assert.Equal(MarkerState.Absent, TlsJournalStore.Read().state);   // journal always cleaned

        // Ownership-checked removal: only the bindings the marker proves are ours.
        Assert.Equal(0, TlsSetup.Remove(TestPort));
        BothBindings(HttpSysSsl.Kind.Absent);
        Assert.False(HelperTls.RuntimeReady(TestPort, out _));   // fails closed afterwards
        Sentinel("elevated-lifecycle", "done=true");
    }

    [Fact]
    public void ForeignBindingOnPortIsNeverTouched()
    {
        if (!Enabled) return;
        Assert.True(IsAdmin);
        // Bind the port to a certificate that is not ours (throwaway self-signed, different app id), then expect setup to refuse and removal to leave it.
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=foreign-test", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var ss = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(ss.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), (string?)null,
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.PersistKeySet | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.MachineKeySet);
        using (var st = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine))
        { st.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite); st.Add(cert); }
        var foreignApp = Guid.NewGuid();
        try
        {
            RunNetsh($"http add sslcert ipport=0.0.0.0:{TestPort} certhash={cert.Thumbprint} appid={{{foreignApp}}} certstorename=MY");
            Assert.NotEqual(0, TlsSetup.Run(TestPort, true));                       // refuses: a binding exists that is not provably ours
            Assert.Equal(HttpSysSsl.Kind.Found, HttpSysSsl.QueryExact_(IPAddress.Any, TestPort).Kind);
            Assert.Equal(0, TlsSetup.Remove(TestPort));
            var r = HttpSysSsl.QueryExact_(IPAddress.Any, TestPort);
            Assert.Equal(HttpSysSsl.Kind.Found, r.Kind);                            // still there
            Assert.Equal(cert.Thumbprint, r.Thumbprint, ignoreCase: true);
            Sentinel("foreign-binding", "done=true");
        }
        finally
        {
            RunNetsh($"http delete sslcert ipport=0.0.0.0:{TestPort}");
            using var st = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            st.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite); st.Remove(cert);
        }
    }

    /// <summary>Workflow step: setup only, leaving a ready identity and bindings for the unelevated check.</summary>
    [Fact]
    public void SetupOnlyLeavesReadyIdentity()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("ZEPHYR_TLS_SETUP_ONLY") != "1") return;
        Assert.True(IsAdmin);
        Assert.Equal(0, TlsSetup.Run(TestPort, true));
        Assert.True(HelperTls.RuntimeReady(TestPort, out var why), why);
        Sentinel("setup-only", "ready=true");
    }

    /// <summary>Run as a standard (non-admin) user by the workflow AFTER the elevated test left a ready identity: the unelevated runtime view must read it as Ready without enumerating or signing.</summary>
    [Fact]
    public void StandardUserRuntimeReadsReadyState()
    {
        // The workflow passes ZEPHYR_TLS_STDUSER and the expected account with `dotnet test -e`, so the child does not depend on inherited environment.
        if (Environment.GetEnvironmentVariable("ZEPHYR_TLS_STDUSER") != "1") return;   // normal runs: not applicable; the workflow requires the sentinel, so an unexercised pass fails there
        Assert.True(OperatingSystem.IsWindows());
        var expected = Environment.GetEnvironmentVariable("ZEPHYR_TLS_EXPECT_USER");
        Assert.False(string.IsNullOrEmpty(expected), "expected account name was not passed");
        Assert.Equal(expected, Environment.UserName, ignoreCase: true);
        Assert.False(IsAdmin, "this check must run with a non-admin token");
        var ready = HelperTls.RuntimeReady(TestPort, out var why);
        Sentinel("standard-user", "ready=" + ready + ";reason=" + why);
        Assert.True(ready, why);
    }

    /// <summary>Proof the test body really ran (an early return reports as passed). The workflow requires each sentinel, so a skipped or unexercised test fails the run. Contains only the test name, account name, elevation flag and readiness reason; no secrets.</summary>
    private static void Sentinel(string name, string extra)
    {
        var dir = Environment.GetEnvironmentVariable("ZEPHYR_TLS_SENTINEL_DIR");
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("ZEPHYR_TLS_SENTINEL_DIR is required when the integration test runs");
        File.WriteAllText(Path.Combine(dir, name + ".txt"), $"user={Environment.UserName};admin={IsAdmin};{extra}");
    }

    private static void RunNetsh(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit(15000);
    }
}
