using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UpdateCoordinator.TestSupport;

/// <summary>
/// Self-signed TLS certificates for tests; never written to a certificate store.
/// </summary>
public static class TestCertificates
{
    public static X509Certificate2 Create(string commonName = "localhost")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        // Round-trip through PKCS#12 so SChannel can use the private key for a TLS handshake.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }
}
