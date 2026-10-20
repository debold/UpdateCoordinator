using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UpdateCoordinator.Contracts.Trust;

/// <summary>
/// Shared definition of what the trust anchor signs, so central server and agent cannot drift apart.
/// </summary>
public static class TrustBindingFormat
{
    /// <summary>Hash algorithm used for certificate hashes and signatures.</summary>
    public static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    /// <summary>Length of a SHA-256 hash in bytes.</summary>
    public const int HashLength = 32;

    /// <summary>Upper bound for the Base64 encoded anchor key and signature; larger values are rejected.</summary>
    public const int MaxEncodedLength = 512;

    private const string Purpose = "UpdateCoordinator.TrustBinding.v1";

    /// <summary>Bytes signed by the anchor: purpose, serial and certificate hash, unambiguously delimited.</summary>
    public static byte[] GetSignedBytes(long serial, ReadOnlySpan<byte> certificateSpkiSha256)
    {
        if (certificateSpkiSha256.Length != HashLength)
        {
            throw new ArgumentException("The certificate hash must be a SHA-256 hash.", nameof(certificateSpkiSha256));
        }

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{Purpose}|{serial}|{Convert.ToHexString(certificateSpkiSha256)}");
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>
    /// Short fingerprint of the trust anchor for the installer file name: the first 16 bytes of the SHA-256 of its
    /// SubjectPublicKeyInfo as lowercase hex (128 bits are far beyond any feasible second preimage).
    /// </summary>
    public static string ComputeAnchorFingerprint(ReadOnlySpan<byte> anchorSubjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(anchorSubjectPublicKeyInfo)[..16]);

    /// <summary>SHA-256 hash of the certificate's public key (SubjectPublicKeyInfo); stays the same when a certificate is renewed with the same key.</summary>
    public static byte[] ComputeSpkiHash(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
    }
}
