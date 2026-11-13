using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Assignment;

/// <summary>A server holding a lease of a coordinated group, and until when.</summary>
internal sealed record LeaseHolder(Guid ClientId, string HostName, DateTime ExpiresAtUtc);

/// <summary>
/// The state of the coordinated groups as the dashboard shows it (Marc, 2026-11-13): stopped or not, and who holds a
/// lease until when, so a server that waits for a lease shows why. One query path for the group list, the group page
/// and the server page.
/// </summary>
internal static class GroupLeaseStatus
{
    /// <summary>Active leases per group, earliest end first.</summary>
    public static async Task<Dictionary<int, List<LeaseHolder>>> HoldersAsync(ServerDbContext db, IReadOnlyCollection<int>? groupIds, CancellationToken cancellationToken)
    {
        var leases = await db.Leases.AsNoTracking()
            .Where(l => l.State == LeaseState.Active && (groupIds == null || groupIds.Contains(l.GroupId)))
            .Join(db.Clients, l => l.ClientId, c => c.Id, (l, c) => new { l.GroupId, l.ClientId, c.HostName, l.ExpiresAtUtc })
            .OrderBy(l => l.ExpiresAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return leases
            .GroupBy(l => l.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(l => new LeaseHolder(l.ClientId, l.HostName ?? l.ClientId.ToString(), l.ExpiresAtUtc)).ToList());
    }
}
