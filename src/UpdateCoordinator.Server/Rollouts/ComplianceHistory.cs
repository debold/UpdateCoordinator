using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Rollouts;

/// <summary>
/// Records the patch compliance once per day for the trend on the overview (docs/06-dashboard.md, section 2):
/// a server is compliant when no released update is left to install. The value of the current day is updated with
/// every run, so the last value of a day remains.
/// </summary>
internal sealed class ComplianceHistory(ServerDbContext db, TimeProvider timeProvider)
{
    public const int DaysKept = 400;

    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var approved = db.Clients.Where(c => c.State == RegistrationState.Approved);
        var servers = await approved.CountAsync(cancellationToken).ConfigureAwait(false);
        var compliant = await approved.CountAsync(c => !db.Releases.Any(r => r.ClientId == c.Id), cancellationToken).ConfigureAwait(false);

        var snapshot = await db.ComplianceSnapshots.SingleOrDefaultAsync(s => s.Day == today, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            db.ComplianceSnapshots.Add(new ComplianceSnapshotEntity { Day = today, Servers = servers, Compliant = compliant });
        }
        else
        {
            snapshot.Servers = servers;
            snapshot.Compliant = compliant;
        }

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
