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
internal sealed class ComplianceHistory(ServerDbContext db, DeploymentStatus deployment, TimeProvider timeProvider, Security.SecurityBaseline? baseline = null)
{
    public const int DaysKept = 400;

    /// <summary>How often the worker records the day; each record evaluates every server.</summary>
    public static readonly TimeSpan RecordInterval = TimeSpan.FromMinutes(15);

    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var current = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await db.ComplianceSnapshots.SingleOrDefaultAsync(s => s.Day == today, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            db.ComplianceSnapshots.Add(current);
        }
        else
        {
            db.Entry(snapshot).CurrentValues.SetValues(current);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.ComplianceSnapshots.Where(s => s.Day < today.AddDays(-DaysKept)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Servers with every update released for them installed: no released installation pending or failed. One place for
    /// the overview tile and the recorded statistics, so both agree.
    /// </summary>
    internal static int Compliant(IEnumerable<ClientEntity> clients, IEnumerable<DeploymentEntry> entries)
    {
        var open = entries.Where(e => e.EarliestInstallAtUtc is not null && e.State != DeploymentState.Successful).Select(e => e.ClientId).ToHashSet();
        return clients.Count(c => !open.Contains(c.Id));
    }

    /// <summary>
    /// The values of today as they are right now, without saving: the overview chart shows them for today, so tile and
    /// chart always agree (the stored record is up to 15 minutes old).
    /// </summary>
    public async Task<ComplianceSnapshotEntity> CaptureAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var clients = await db.Clients.AsNoTracking().Where(c => c.State == RegistrationState.Approved).ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = (await db.ClientAvailableUpdates.AsNoTracking().Select(a => a.ClientId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var entries = await deployment.GetAsync(null, null, null, cancellationToken).ConfigureAwait(false);
        var failed = entries.Where(e => e.State == DeploymentState.Failed).Select(e => e.ClientId).ToHashSet();
        var health = clients.Select(c => ServerHealth.Evaluate(c, failed.Contains(c.Id), now)).ToList();
        int Count(ServerHealthState state) => health.Count(h => h == state);
        var snapshot = baseline is null ? Security.SecuritySnapshot.Empty : await baseline.LoadAsync(cancellationToken).ConfigureAwait(false);
        var security = clients.Select(snapshot.Of).ToList();
        var known = security.Count(s => s.State != Security.BaselineState.Unknown);
        return new ComplianceSnapshotEntity
        {
            Day = DateOnly.FromDateTime(now),
            RecordedAtUtc = now,
            Servers = clients.Count,
            Compliant = Compliant(clients, entries),
            FullyPatched = clients.Count(c => !missing.Contains(c.Id)),
            StatusOk = Count(ServerHealthState.Ok),
            StatusUpdating = Count(ServerHealthState.Updating),
            StatusRebootPending = Count(ServerHealthState.RebootPending),
            StatusWarning = Count(ServerHealthState.Warning),
            StatusFailed = Count(ServerHealthState.Failed),
            StatusOffline = Count(ServerHealthState.Offline),
            OpenReleased = entries.Count(e => e.EarliestInstallAtUtc is not null && e.State != DeploymentState.Successful),
            OpenHeld = entries.Count(e => e.State == DeploymentState.Queued),
            BaselineCurrent = known == 0 ? null : security.Count(s => s.State == Security.BaselineState.Current),
            BaselineKnown = known == 0 ? null : known,
            OpenVulnerabilities = known == 0 ? null : security.Sum(s => s.Open.Count),
            OpenExploited = known == 0 ? null : security.Sum(s => s.Exploited),
        };
    }

    /// <summary>Snapshots of the last <paramref name="days"/> days, oldest first.</summary>
    public Task<List<ComplianceSnapshotEntity>> GetAsync(int days, CancellationToken cancellationToken)
    {
        var from = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(-days);
        return db.ComplianceSnapshots.AsNoTracking().Where(s => s.Day > from).OrderBy(s => s.Day).ToListAsync(cancellationToken);
    }
}
