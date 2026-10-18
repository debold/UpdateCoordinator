namespace UpdateCoordinator.Contracts.Trust;

/// <summary>
/// Binding between the trust anchor of the central server and its current TLS certificate
/// (see docs/03-communication-protocol.md, section 3.2). All binary values are Base64 encoded.
/// </summary>
/// <param name="AnchorPublicKey">Public key of the trust anchor (SubjectPublicKeyInfo, DER).</param>
/// <param name="CertificateSpkiSha256">SHA-256 hash of the TLS certificate's SubjectPublicKeyInfo.</param>
/// <param name="Serial">Increasing serial number; a new certificate always gets a higher serial.</param>
/// <param name="Signature">ECDSA P-256/SHA-256 signature by the anchor over <see cref="TrustBindingFormat.GetSignedBytes"/>.</param>
public sealed record TrustBinding(string AnchorPublicKey, string CertificateSpkiSha256, long Serial, string Signature);
