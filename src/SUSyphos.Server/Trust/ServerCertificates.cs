using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SUSyphos.Server.Trust;

/// <summary>
/// Lifetime of the certificate the central server creates for itself and how renewals are followed. Bound from the
/// "Certificate" configuration section.
/// </summary>
internal sealed class CertificateOptions : IValidatableObject
{
    public const string SectionName = "Certificate";

    /// <summary>Validity of a self-signed certificate created by the central server.</summary>
    [Range(typeof(TimeSpan), "7.00:00:00", "3650.00:00:00")]
    public TimeSpan SelfSignedLifetime { get; set; } = TimeSpan.FromDays(180);

    /// <summary>A self-signed certificate is replaced this long before it expires; a CA certificate is reported as expiring.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "180.00:00:00")]
    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often the certificate store is checked for a renewed certificate.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RenewBefore * 2 > SelfSignedLifetime)
        {
            yield return new ValidationResult("RenewBefore must be at most half of SelfSignedLifetime.", [nameof(RenewBefore)]);
        }
    }
}

/// <summary>Shared helpers for TLS certificates of the central server.</summary>
internal static class ServerCertificates
{
    /// <summary>Friendly name that marks a certificate the central server created for itself.</summary>
    public const string SelfGeneratedFriendlyName = "SUSyphos self-signed";

    public const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>True for a self-signed certificate the central server created (and may therefore replace on its own).</summary>
    public static bool IsSelfGenerated(X509Certificate2 certificate) =>
        certificate.FriendlyName == SelfGeneratedFriendlyName && certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    public static bool AllowsServerAuthentication(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is null || usage.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthenticationOid);
    }

    public static CertificateCandidate Describe(X509Certificate2 certificate) => new(
        certificate.Thumbprint,
        certificate.Subject,
        certificate.Issuer,
        certificate.NotBefore.ToUniversalTime(),
        certificate.NotAfter.ToUniversalTime(),
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames().ToList() ?? [],
        IsSelfGenerated(certificate));

    /// <summary>Request for a TLS server certificate: CN is the first name, all names in the SAN.</summary>
    public static CertificateRequest CreateRequest(IReadOnlyList<string> dnsNames, RSA key)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dnsNames.Count);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={dnsNames[0]}"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthenticationOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request;
    }

    /// <summary>
    /// Renewal of <paramref name="current"/> among the candidates: same subject and issuer, at least the same DNS names and a
    /// later expiry, as Windows autoenrollment or an administrator renewing at the CA produces it. A foreign self-signed
    /// certificate is never followed, since anyone with local admin rights could create one with the same subject.
    /// </summary>
    public static CertificateCandidate? FindSuccessor(CertificateCandidate current, IEnumerable<CertificateCandidate> candidates)
    {
        var selfSigned = string.Equals(current.Subject, current.Issuer, StringComparison.Ordinal);
        if (selfSigned && !current.SelfGenerated)
        {
            return null;
        }

        return candidates
            .Where(c => !string.Equals(c.Thumbprint, current.Thumbprint, StringComparison.OrdinalIgnoreCase)
                && c.SelfGenerated == current.SelfGenerated
                && string.Equals(c.Subject, current.Subject, StringComparison.Ordinal)
                && string.Equals(c.Issuer, current.Issuer, StringComparison.Ordinal)
                && c.NotAfterUtc > current.NotAfterUtc
                && current.DnsNames.All(n => c.DnsNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.NotAfterUtc)
            .FirstOrDefault();
    }
}
