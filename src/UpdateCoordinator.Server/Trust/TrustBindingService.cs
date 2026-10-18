using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Trust;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Trust;

/// <summary>
/// Keeps a valid binding for the current TLS certificate and serves the latest one.
/// </summary>
internal sealed partial class TrustBindingService(
    ServerDbContext db,
    TrustAnchor anchor,
    ITlsCertificateProvider certificateProvider,
    TimeProvider timeProvider,
    ILogger<TrustBindingService> logger)
{
    /// <summary>
    /// Creates a new binding with the next serial if the certificate changed or the latest binding
    /// is not signed by the current anchor. Called on startup.
    /// </summary>
    public async Task EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        var hash = TrustBindingFormat.ComputeSpkiHash(certificateProvider.Certificate);
        var latest = await LatestAsync(cancellationToken).ConfigureAwait(false);

        if (latest is not null
            && latest.CertificateSpkiSha256.AsSpan().SequenceEqual(hash)
            && anchor.Verify(TrustBindingFormat.GetSignedBytes(latest.Serial, hash), latest.Signature))
        {
            return;
        }

        var serial = (latest?.Serial ?? 0) + 1;
        db.TrustBindings.Add(new TrustBindingEntity
        {
            Serial = serial,
            CertificateSpkiSha256 = hash,
            Signature = anchor.Sign(TrustBindingFormat.GetSignedBytes(serial, hash)),
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = Convert.ToHexString(hash);
        LogBindingCreated(serial, fingerprint);
    }

    public async Task<TrustBinding?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var latest = await LatestAsync(cancellationToken).ConfigureAwait(false);
        return latest is null
            ? null
            : new TrustBinding(
                Convert.ToBase64String(anchor.PublicKey),
                Convert.ToBase64String(latest.CertificateSpkiSha256),
                latest.Serial,
                Convert.ToBase64String(latest.Signature));
    }

    private Task<TrustBindingEntity?> LatestAsync(CancellationToken cancellationToken) =>
        db.TrustBindings.AsNoTracking().OrderByDescending(b => b.Serial).FirstOrDefaultAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed trust binding {Serial} for the TLS certificate key {SpkiSha256}.")]
    private partial void LogBindingCreated(long serial, string spkiSha256);
}
