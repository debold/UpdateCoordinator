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
    ServerDbContext db, UpdateCatalog catalog, RolloutService rollouts, ReleasePlanner releases, ClientActivityLog activity, Scripts.ScriptAdministration scripts, TimeProvider timeProvider, ILogger<UpdateReportService> logger)
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
        var client = await db.Clients.Where(c => c.Id == clientId).Select(c => new { c.ReleasesVersion, c.RingId, c.EffectiveWindowsJson, c.EffectiveRebootPolicy, c.RequiresLease, c.EffectiveManageAutomaticUpdates, c.EffectiveUseMicrosoftUpdate, c.EffectiveAgentSettingsJson, c.EffectiveSoftwareWindowsJson }).SingleAsync(cancellationToken).ConfigureAwait(false);
        var oneTime = await Assignment.OneTimeWindows.ForClientAsync(db, clientId, client.RingId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
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

        // Software versions carry package and version, so the agent installs them with WinGet.
        var updateIds = rows.Select(r => r.UpdateId).ToList();
        var software = (await (from u in db.Updates.AsNoTracking()
                               join p in db.Packages.AsNoTracking() on u.PackageId equals p.PackageId
                               where u.Kind == UpdateKind.Software && updateIds.Contains(u.UpdateId)
                               select new { u.UpdateId, p.PackageId, u.PackageVersion, Accepted = p.AgreementsAcceptedAtUtc != null })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(s => s.UpdateId, s => new SoftwareRelease(s.PackageId, s.PackageVersion!, s.Accepted));

        // The installed version tells the agent (and its scripts) whether it is an installation or an upgrade.
        var packageIds = software.Values.Select(s => s.PackageId).ToList();
        // With several installations of a package (x64 and x86 of a runtime) the oldest one is the one to update; its name
        // tells its architecture, so WinGet updates it instead of installing the other platform (lab, 2026-11-16).
        var installed = (await db.ClientSoftware.AsNoTracking().Where(s => s.ClientId == clientId && s.PackageId != null && packageIds.Contains(s.PackageId)).Select(s => new { s.PackageId, Version = s.InstalledVersion ?? s.DisplayVersion, s.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(s => s.PackageId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Version ?? string.Empty, SUSyphos.WinGet.Catalog.RestSource.VersionComparer.Instance).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (updateId, release) in software.ToList())
        {
            if (installed.GetValueOrDefault(release.PackageId) is { } program)
            {
                software[updateId] = release with { InstalledVersion = program.Version, Architecture = SoftwareArchitecture.FromName(program.DisplayName) };
            }
        }

        // Approved scripts of the packages for the installation phases, with this server's values (Marc, 2026-11-13).
        foreach (var packageId in software.Values.Select(s => s.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var references = await scripts.ReferencesAsync(packageId, clientId, [SUSyphos.Contracts.Scripts.ScriptPhase.BeforeInstall, SUSyphos.Contracts.Scripts.ScriptPhase.AfterInstall], cancellationToken).ConfigureAwait(false);
            if (references.Count > 0)
            {
                foreach (var (updateId, release) in software.Where(s => string.Equals(s.Value.PackageId, packageId, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    software[updateId] = release with { Scripts = references };
                }
            }
        }

        return new ReleaseList(
            client.ReleasesVersion,
            [.. rows.Select(r => new Release(r.UpdateId, r.Revision, new DateTimeOffset(DateTime.SpecifyKind(r.EarliestInstallAtUtc, DateTimeKind.Utc)), r.OutsideWindows, software.GetValueOrDefault(r.UpdateId)))],
            MaintenanceWindowFormat.FromJson(client.EffectiveWindowsJson),
            client.EffectiveRebootPolicy,
            client.RequiresLease,
            client.EffectiveManageAutomaticUpdates,
            client.EffectiveUseMicrosoftUpdate,
            client.EffectiveAgentSettingsJson is null ? null : AgentSettingValues.Parse(client.EffectiveAgentSettingsJson),
            oneTime.Count == 0 ? null : oneTime,
            !await Software.SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false),
            MaintenanceWindowFormat.FromJson(client.EffectiveSoftwareWindowsJson));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId}: pre-flight check failed ({Checks}).")]
    private partial void LogHealthFailed(Guid clientId, string checks);
}
