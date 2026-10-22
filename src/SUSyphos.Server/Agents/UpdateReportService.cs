using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Agents;

/// <summary>
/// Stores update reports (pre-flight checks and available updates) and serves releases.
/// </summary>
internal sealed partial class UpdateReportService(
    ServerDbContext db, UpdateCatalog catalog, RolloutService rollouts, ReleasePlanner releases, ClientActivityLog activity, TimeProvider timeProvider, ILogger<UpdateReportService> logger)
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
        List<(Guid UpdateId, int Revision)> reportedKeys = [];
        if (report.SearchSucceeded && report.AvailableUpdates is { } available)
        {
            var reported = available.DistinctBy(u => (u.UpdateId, u.Revision)).ToList();
            reportedKeys = [.. reported.Select(u => (u.UpdateId, u.Revision))];
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

            // Released updates the agent looked up and Windows Update no longer knows: candidates for a withdrawal.
            if (report.NotOffered is { } notOffered)
            {
                var gone = notOffered.Select(u => (u.UpdateId, u.Revision)).ToHashSet();
                foreach (var release in await db.Releases.Where(r => r.ClientId == clientId).ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    release.NotOfferedAtUtc = gone.Contains((release.UpdateId, release.Revision)) ? release.NotOfferedAtUtc ?? now : null;
                }
            }
        }

        activity.Add(clientId, ClientActivityKind.Search, report.SearchSucceeded
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Update search succeeded: {report.AvailableUpdates?.Count ?? 0} updates available.")
            : report.HealthChecks.Any(check => check.Status == HealthStatus.Failed)
                ? $"Update search skipped: pre-flight check failed ({string.Join(", ", report.HealthChecks.Where(c => c.Status == HealthStatus.Failed).Select(c => c.Name))})."
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Update search failed: 0x{report.SearchHResult ?? 0:X8} {HResultCatalog.Describe(report.SearchHResult ?? 0)}"));

        if (report.HealthChecks.Any(check => check.Status == HealthStatus.Failed))
        {
            LogHealthFailed(clientId, string.Join(", ", report.HealthChecks.Where(c => c.Status == HealthStatus.Failed).Select(c => c.Name)));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // New updates of configured classifications start their rollout; the client gets what is released for its ring.
        await rollouts.AutoStartAsync(reportedKeys, cancellationToken).ConfigureAwait(false);
        await rollouts.CheckWithdrawnAsync(cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(clientId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ReleaseList> GetReleasesAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.Clients.Where(c => c.Id == clientId).Select(c => new { c.ReleasesVersion, c.EffectiveWindowsJson, c.EffectiveRebootPolicy, c.RequiresLease, c.EffectiveManageAutomaticUpdates, c.EffectiveUseMicrosoftUpdate }).SingleAsync(cancellationToken).ConfigureAwait(false);
        // Outside the windows only if the rollout allows it and Windows reports the update as never restarting.
        var neverReboots = (int)RebootBehavior.NeverReboots;
        var rows = await db.Releases
            .Where(r => r.ClientId == clientId)
            .OrderBy(r => r.EarliestInstallAtUtc)
            .Select(r => new
            {
                r.UpdateId,
                r.Revision,
                r.EarliestInstallAtUtc,
                OutsideWindows = db.Rollouts.Any(o => o.UpdateId == r.UpdateId && o.Revision == r.Revision && o.OutsideWindows)
                    && db.Updates.Any(u => u.UpdateId == r.UpdateId && u.Revision == r.Revision && u.RebootBehavior == neverReboots),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ReleaseList(
            client.ReleasesVersion,
            [.. rows.Select(r => new Release(r.UpdateId, r.Revision, new DateTimeOffset(DateTime.SpecifyKind(r.EarliestInstallAtUtc, DateTimeKind.Utc)), r.OutsideWindows))],
            MaintenanceWindowFormat.FromJson(client.EffectiveWindowsJson),
            client.EffectiveRebootPolicy,
            client.RequiresLease,
            client.EffectiveManageAutomaticUpdates,
            client.EffectiveUseMicrosoftUpdate);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId}: pre-flight check failed ({Checks}).")]
    private partial void LogHealthFailed(Guid clientId, string checks);
}
