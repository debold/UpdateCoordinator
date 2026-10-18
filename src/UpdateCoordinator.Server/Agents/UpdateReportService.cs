using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Stores update reports (pre-flight checks and available updates) and serves releases.
/// </summary>
internal sealed partial class UpdateReportService(ServerDbContext db, UpdateCatalog catalog, TimeProvider timeProvider, ILogger<UpdateReportService> logger)
{
    /// <summary>Returns false if the client no longer exists.</summary>
    public async Task<bool> StoreAsync(Guid clientId, UpdateReport report, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return false;
        }

        // The server's receive time is authoritative; the agent clock is not trusted for ordering.
        client.LastSearchAtUtc = now;
        client.LastSearchSucceeded = report.SearchSucceeded;
        client.LastSearchHResult = report.SearchSucceeded ? null : report.SearchHResult;
        client.HealthChecks = [.. report.HealthChecks.Select(check => new HealthCheckEntry { Name = check.Name, Status = (int)check.Status, Detail = check.Detail })];

        // Without a successful search the previous list stays valid.
        if (report.SearchSucceeded && report.AvailableUpdates is { } available)
        {
            var reported = available.DistinctBy(u => (u.UpdateId, u.Revision)).ToList();
            await catalog.EnsureAsync(reported, now, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await db.ClientAvailableUpdates.Where(a => a.ClientId == clientId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.ClientAvailableUpdates.AddRange(reported.Select(u => new ClientAvailableUpdateEntity
            {
                ClientId = clientId,
                UpdateId = u.UpdateId,
                Revision = u.Revision,
                ReportedAtUtc = now,
            }));
        }

        if (report.HealthChecks.Any(check => check.Status == HealthStatus.Failed))
        {
            LogHealthFailed(clientId, string.Join(", ", report.HealthChecks.Where(c => c.Status == HealthStatus.Failed).Select(c => c.Name)));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ReleaseList> GetReleasesAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var version = await db.Clients.Where(c => c.Id == clientId).Select(c => c.ReleasesVersion).SingleAsync(cancellationToken).ConfigureAwait(false);
        var releases = await db.Releases
            .Where(r => r.ClientId == clientId)
            .OrderBy(r => r.EarliestInstallAtUtc)
            .Select(r => new { r.UpdateId, r.Revision, r.EarliestInstallAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ReleaseList(
            version,
            [.. releases.Select(r => new Release(r.UpdateId, r.Revision, new DateTimeOffset(DateTime.SpecifyKind(r.EarliestInstallAtUtc, DateTimeKind.Utc))))]);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId}: pre-flight check failed ({Checks}).")]
    private partial void LogHealthFailed(Guid clientId, string checks);
}
