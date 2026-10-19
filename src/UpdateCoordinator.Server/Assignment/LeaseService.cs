using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Assignment;

/// <summary>
/// Leases for coordinated groups (docs/04-central-server.md, group coordination): only a share of the members may install
/// and reboot at the same time. Coordination runs centrally; a lease expires, so a hanging server never blocks the group.
/// Failed or expired leases in a row stop the group until an operator resumes it.
/// </summary>
internal sealed partial class LeaseService(ServerDbContext db, AuditLog audit, TimeProvider timeProvider, ILogger<LeaseService> logger)
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(2);

    /// <summary>Failed leases in a row that stop a group; small groups stop after as many failures as they have members.</summary>
    public const int FailureThreshold = 2;

    /// <summary>Members that may hold a lease at the same time: the percentage of the group, at most the maximum, at least one.</summary>
    public static int Capacity(int members, int percent, int maximum) =>
        Math.Max(1, Math.Min(maximum, members * percent / 100));

    public static int Threshold(int members) => Math.Max(1, Math.Min(FailureThreshold, members));

    /// <summary>Grants leases in every coordinated group of the client at once, or none.</summary>
    public async Task<LeaseResponse> AcquireAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var groups = await CoordinatedGroupsAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (groups.Count == 0)
        {
            // Not coordinated: no lease needed.
            return new LeaseResponse(true, null);
        }

        // Serialized, so two servers cannot take the last free lease at the same time.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var held = await db.Leases.Where(l => l.ClientId == clientId && l.State == LeaseState.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (groups.All(g => held.Any(l => l.GroupId == g.Id)))
        {
            return new LeaseResponse(true, new DateTimeOffset(DateTime.SpecifyKind(held.Min(l => l.ExpiresAtUtc), DateTimeKind.Utc)));
        }

        foreach (var group in groups)
        {
            if (group.LeasesHalted)
            {
                return new LeaseResponse(false, null, $"Group {group.Name} is stopped after failed leases.");
            }

            var members = await db.ClientGroups.CountAsync(m => m.GroupId == group.Id, cancellationToken).ConfigureAwait(false);
            var active = await db.Leases.CountAsync(l => l.GroupId == group.Id && l.State == LeaseState.Active && l.ClientId != clientId, cancellationToken).ConfigureAwait(false);
            if (active >= Capacity(members, group.LeasePercent, group.LeaseMaximum))
            {
                return new LeaseResponse(false, null, $"All leases of group {group.Name} are taken.");
            }
        }

        var expires = now + Duration;
        db.Leases.AddRange(groups.Where(g => !held.Any(l => l.GroupId == g.Id)).Select(g => new LeaseEntity
        {
            GroupId = g.Id,
            ClientId = clientId,
            State = LeaseState.Active,
            IssuedAtUtc = now,
            ExpiresAtUtc = expires,
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LeaseResponse(true, new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)));
    }

    /// <summary>Ends the active leases of the client; false if it holds none.</summary>
    public async Task<bool> ReleaseAsync(Guid clientId, bool succeeded, CancellationToken cancellationToken)
    {
        var leases = await db.Leases.Where(l => l.ClientId == clientId && l.State == LeaseState.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (leases.Count == 0)
        {
            return false;
        }

        await EndAsync(leases, succeeded ? LeaseState.Succeeded : LeaseState.Failed, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Expires leases past their end; an expired lease counts as a failure.</summary>
    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = await db.Leases.Where(l => l.State == LeaseState.Active && l.ExpiresAtUtc < now).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (expired.Count > 0)
        {
            await EndAsync(expired, LeaseState.Expired, cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    public async Task<Outcome> ResumeAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is not { LeasesHalted: true })
        {
            return new Outcome("The group is not stopped.");
        }

        group.LeasesHalted = false;
        group.ConsecutiveLeaseFailures = 0;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Group.LeasesResumed", group.Name, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private async Task EndAsync(List<LeaseEntity> leases, LeaseState state, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var groupIds = leases.Select(l => l.GroupId).Distinct().ToList();
        var groups = await db.Groups.Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, cancellationToken).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            lease.State = state;
            lease.EndedAtUtc = now;
            if (!groups.TryGetValue(lease.GroupId, out var group))
            {
                continue;
            }

            if (state == LeaseState.Succeeded)
            {
                group.ConsecutiveLeaseFailures = 0;
                continue;
            }

            group.ConsecutiveLeaseFailures++;
            var members = await db.ClientGroups.CountAsync(m => m.GroupId == group.Id, cancellationToken).ConfigureAwait(false);
            if (!group.LeasesHalted && group.ConsecutiveLeaseFailures >= Threshold(members))
            {
                group.LeasesHalted = true;
                LogHalted(group.Name, group.ConsecutiveLeaseFailures);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<List<GroupEntity>> CoordinatedGroupsAsync(Guid clientId, CancellationToken cancellationToken) =>
        db.Groups
            .Where(g => g.Coordinated && db.ClientGroups.Any(m => m.ClientId == clientId && m.GroupId == g.Id))
            .OrderBy(g => g.Id)
            .ToListAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Coordinated group {Group} stopped after {Failures} failed or expired leases in a row; an operator must resume it.")]
    private partial void LogHalted(string group, int failures);
}
