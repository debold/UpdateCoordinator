using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>
/// Records the state of each day for the statistics on the overview (docs/06-dashboard.md, section 2): patch compliance
/// (no released update left to install), fully patched (no update missing at all), servers by status and open
/// server × update pairs. The value of the current day is updated with every run, so the last value of a day remains.
/// </summary>
internal sealed class ComplianceHistory(ServerDbContext db, DeploymentStatus deployment, TimeProvider timeProvider)
{
    public const int DaysKept = 400;

    /// <summary>How often the worker records the day; each record evaluates every server.</summary>
    public static readonly TimeSpan RecordInterval = TimeSpan.FromMinutes(15);

    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var snapshot = await db.ComplianceSnapshots.SingleOrDefaultAsync(s => s.Day == today, cancellationToken).ConfigureAwait(false);
        var clients = await db.Clients.AsNoTracking().Where(c => c.State == RegistrationState.Approved).ToListAsync(cancellationToken).ConfigureAwait(false);
        var withRelease = (await db.Releases.AsNoTracking().Select(r => r.ClientId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var missing = (await db.ClientAvailableUpdates.AsNoTracking().Select(a => a.ClientId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var entries = await deployment.GetAsync(null, null, null, cancellationToken).ConfigureAwait(false);
        var failed = entries.Where(e => e.State == DeploymentState.Failed).Select(e => e.ClientId).ToHashSet();
        var health = clients.Select(c => ServerHealth.Evaluate(c, failed.Contains(c.Id), now)).ToList();
        int Count(ServerHealthState state) => health.Count(h => h == state);

        if (snapshot is null)
        {
            snapshot = new ComplianceSnapshotEntity { Day = today };
            db.ComplianceSnapshots.Add(snapshot);
        }

        snapshot.RecordedAtUtc = now;
        snapshot.Servers = clients.Count;
        snapshot.Compliant = clients.Count(c => !withRelease.Contains(c.Id));
        snapshot.FullyPatched = clients.Count(c => !missing.Contains(c.Id));
        snapshot.StatusOk = Count(ServerHealthState.Ok);
        snapshot.StatusUpdating = Count(ServerHealthState.Updating);
        snapshot.StatusRebootPending = Count(ServerHealthState.RebootPending);
        snapshot.StatusWarning = Count(ServerHealthState.Warning);
        snapshot.StatusFailed = Count(ServerHealthState.Failed);
        snapshot.StatusOffline = Count(ServerHealthState.Offline);
        snapshot.OpenReleased = entries.Count(e => e.EarliestInstallAtUtc is not null && e.State != DeploymentState.Successful);
        snapshot.OpenHeld = entries.Count(e => e.State == DeploymentState.Queued);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.ComplianceSnapshots.Where(s => s.Day < today.AddDays(-DaysKept)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Snapshots of the last <paramref name="days"/> days, oldest first.</summary>
    public Task<List<ComplianceSnapshotEntity>> GetAsync(int days, CancellationToken cancellationToken)
    {
        var from = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(-days);
        return db.ComplianceSnapshots.AsNoTracking().Where(s => s.Day > from).OrderBy(s => s.Day).ToListAsync(cancellationToken);
    }
}
