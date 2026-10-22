using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using SUSyphos.Server.Trust;

namespace SUSyphos.TestSupport;

/// <summary>In-memory stand-in for LocalMachine\My and the registry setting; tests never touch the machine store.</summary>
internal sealed class FakeCertificateStore : IServerCertificateStore
{
    private readonly List<X509Certificate2> _certificates = [];

    /// <summary>Time used for validity checks and created certificates; tests move it to simulate expiry.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Last thumbprint persisted as the configured certificate.</summary>
    public string? Saved { get; private set; }

    public int CreatedCount { get; private set; }

    public X509Certificate2 Add(X509Certificate2 certificate)
    {
        _certificates.Add(certificate);
        return certificate;
    }

    public IReadOnlyList<CertificateCandidate> ListCandidates()
    {
        var now = Clock().UtcDateTime;
        return [.. _certificates
            .Where(c => c.HasPrivateKey && c.NotBefore.ToUniversalTime() <= now && c.NotAfter.ToUniversalTime() > now && ServerCertificates.AllowsServerAuthentication(c))
            .Select(ServerCertificates.Describe)
            .OrderByDescending(c => c.NotAfterUtc)];
    }

    public IReadOnlyList<CertificateCandidate> ListAll()
    {
        var now = Clock().UtcDateTime;
        return ServerCertificates.Order(_certificates.Select(c => ServerCertificates.Describe(c) with { Unusable = ServerCertificates.UnusableReason(c, now) }));
    }

    public X509Certificate2? Load(string thumbprint) =>
        _certificates.FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    public void SaveThumbprint(string thumbprint) => Saved = thumbprint;

    public X509Certificate2 CreateSelfSigned(TimeSpan lifetime)
    {
        CreatedCount++;
        var now = Clock();
        return Add(CreateCertificate(["uc01.lab.local", "uc01"], now.AddMinutes(-10), now + lifetime, selfGenerated: true));
    }

    /// <summary>A server certificate with private key; issued by <paramref name="issuer"/> when given, else self-signed.</summary>
    public static X509Certificate2 CreateCertificate(IReadOnlyList<string> dnsNames, DateTimeOffset notBefore, DateTimeOffset notAfter, bool selfGenerated = false, X509Certificate2? issuer = null)
    {
        using var key = RSA.Create(2048);
        var request = ServerCertificates.CreateRequest(dnsNames, key);
        X509Certificate2 certificate;
        if (issuer is null)
        {
            certificate = request.CreateSelfSigned(notBefore, notAfter);
        }
        else
        {
            using var signed = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
            certificate = signed.CopyWithPrivateKey(key);
        }

        if (selfGenerated)
        {
            certificate.FriendlyName = ServerCertificates.SelfGeneratedFriendlyName;
        }

        return certificate;
    }

    /// <summary>A CA certificate that can issue server certificates in tests.</summary>
    public static X509Certificate2 CreateAuthority(string name = "Lab Issuing CA")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(10));
    }
}
