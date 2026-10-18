namespace UpdateCoordinator.Server.Data;

/// <summary>
/// A signed binding between the trust anchor and a TLS certificate. Serials only ever increase.
/// </summary>
internal sealed class TrustBindingEntity
{
    public long Serial { get; set; }

    public required byte[] CertificateSpkiSha256 { get; set; }

    public required byte[] Signature { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
