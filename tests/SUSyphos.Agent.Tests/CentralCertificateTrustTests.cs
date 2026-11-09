using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Agent.Trust;

namespace SUSyphos.Agent.Tests;

/// <summary>The opt-in that lets WinGet trust the central server's own certificate; nothing here touches the machine.</summary>
public sealed class CentralCertificateTrustTests
{
    private static byte[] SelfSigned(string name, bool certificateAuthority = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (certificateAuthority)
        {
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        }

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.RawData;
    }

    private static byte[] IssuedByCa()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Lab CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=central.lab.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), [1, 2, 3, 4]);
        return issued.RawData;
    }

    private static CentralCertificateTrust Trust(FakeSource source, FakeStore store, bool enabled) =>
        new(source, new FixedMonitor(new SoftwareAgentOptions { TrustCentralCertificate = enabled }), store, NullLogger<CentralCertificateTrust>.Instance);

    [Fact]
    public void OptIn_AddsTheVerifiedSelfSignedCertificate_Once()
    {
        var source = new FakeSource { AcceptedCertificate = SelfSigned("central.lab.local") };
        var store = new FakeStore();

        Trust(source, store, enabled: true).Apply();
        Trust(source, store, enabled: true).Apply();

        var added = Assert.Single(store.Certificates);
        Assert.Equal(store.AddedThumbprint, added);
        Assert.Equal(1, store.Adds);
    }

    [Fact]
    public void WithoutOptIn_NothingIsAdded_AndSwitchingOffRemovesWhatWasAdded()
    {
        var source = new FakeSource { AcceptedCertificate = SelfSigned("central.lab.local") };
        var store = new FakeStore();
        Trust(source, store, enabled: false).Apply();
        Assert.Empty(store.Certificates);

        Trust(source, store, enabled: true).Apply();
        Trust(source, store, enabled: false).Apply();

        Assert.Empty(store.Certificates);
        Assert.Null(store.AddedThumbprint);
    }

    [Fact]
    public void ChangedCertificate_ReplacesTheOldOne()
    {
        var source = new FakeSource { AcceptedCertificate = SelfSigned("central.lab.local") };
        var store = new FakeStore();
        Trust(source, store, enabled: true).Apply();
        var first = store.AddedThumbprint;

        source.AcceptedCertificate = SelfSigned("central.lab.local");
        Trust(source, store, enabled: true).Apply();

        Assert.NotEqual(first, store.AddedThumbprint);
        Assert.Equal([store.AddedThumbprint!], store.Certificates);
    }

    [Fact]
    public void CertificatesOfACa_AndCaCertificates_AreNeverAdded()
    {
        var store = new FakeStore();

        Trust(new FakeSource { AcceptedCertificate = IssuedByCa() }, store, enabled: true).Apply();
        Trust(new FakeSource { AcceptedCertificate = SelfSigned("Evil Root", certificateAuthority: true) }, store, enabled: true).Apply();
        Trust(new FakeSource(), store, enabled: true).Apply();

        Assert.Empty(store.Certificates);
    }

    [Fact]
    public void AlreadyTrustedCertificate_IsNotAddedOrRemovedByTheAgent()
    {
        var raw = SelfSigned("central.lab.local");
        using var certificate = X509CertificateLoader.LoadCertificate(raw);
        var store = new FakeStore();
        store.Certificates.Add(certificate.Thumbprint);

        Trust(new FakeSource { AcceptedCertificate = raw }, store, enabled: true).Apply();
        Trust(new FakeSource { AcceptedCertificate = raw }, store, enabled: false).Apply();

        Assert.Equal(0, store.Adds);
        Assert.Equal([certificate.Thumbprint], store.Certificates);
    }

    private sealed class FakeSource : IAcceptedCertificateSource
    {
        public byte[]? AcceptedCertificate { get; set; }
    }

    private sealed class FakeStore : ICentralCertificateStore
    {
        public List<string> Certificates { get; } = [];

        public int Adds { get; private set; }

        public string? AddedThumbprint { get; set; }

        public bool Contains(string thumbprint) => Certificates.Contains(thumbprint, StringComparer.OrdinalIgnoreCase);

        public void Add(X509Certificate2 certificate)
        {
            Adds++;
            Certificates.Add(certificate.Thumbprint);
        }

        public void Remove(string thumbprint) => Certificates.RemoveAll(c => string.Equals(c, thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FixedMonitor(SoftwareAgentOptions value) : IOptionsMonitor<SoftwareAgentOptions>
    {
        public SoftwareAgentOptions CurrentValue => value;

        public SoftwareAgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SoftwareAgentOptions, string?> listener) => null;
    }
}
