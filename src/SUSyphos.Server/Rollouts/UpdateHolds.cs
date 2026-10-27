using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>What a hold applies to.</summary>
internal enum HoldTarget
{
    Ring,
    Group,
    Server,
}

/// <summary>An active hold as the dashboard shows it: target kind and name, reason, end, who and when.</summary>
internal sealed record HoldView(int Id, HoldTarget Target, string TargetName, string? Reason, DateTime? UntilUtc, string CreatedBy, DateTime CreatedAtUtc);

/// <summary>
/// Holds an update for a ring, a group or a server (Marc, 2026-10-27, docs/04-central-server.md): the release planner
/// withholds the releases there, a held ring does not count as done for the next ring, and "Resume" or the end date lifts it.
/// Hold, stop rollout and block stay different: a hold is for one target and reversible.
/// </summary>
internal sealed class UpdateHolds(ServerDbContext db, ReleasePlanner releases, AuditLog audit, TimeProvider timeProvider)
{
    /// <summary>The active holds, for the release planner and the rollout; one query, filtered in memory.</summary>
    public static async Task<List<UpdateHoldEntity>> ActiveAsync(ServerDbContext db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var holds = await db.UpdateHolds.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. holds.Where(h => h.IsActive(nowUtc))];
    }

    /// <summary>The active holds of an update with the names of their targets, newest first.</summary>
    public async Task<List<HoldView>> GetAsync(Guid updateId, int revision, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var holds = (await db.UpdateHolds.AsNoTracking().Where(h => h.UpdateId == updateId && h.Revision == revision).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(h => h.IsActive(now))
            .ToList();
        var rings = await db.Rings.AsNoTracking().ToDictionaryAsync(r => r.Id, r => $"{r.Order} {r.Name}", cancellationToken).ConfigureAwait(false);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken).ConfigureAwait(false);
        var clientIds = holds.Where(h => h.ClientId is not null).Select(h => h.ClientId!.Value).ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.HostName, cancellationToken).ConfigureAwait(false);
        return [.. holds
            .Select(h => h.RingId is { } ring ? new HoldView(h.Id, HoldTarget.Ring, rings.GetValueOrDefault(ring, "?"), h.Reason, h.UntilUtc, h.CreatedBy, h.CreatedAtUtc)
                : h.GroupId is { } group ? new HoldView(h.Id, HoldTarget.Group, groups.GetValueOrDefault(group, "?"), h.Reason, h.UntilUtc, h.CreatedBy, h.CreatedAtUtc)
                : new HoldView(h.Id, HoldTarget.Server, clients.GetValueOrDefault(h.ClientId!.Value, "?"), h.Reason, h.UntilUtc, h.CreatedBy, h.CreatedAtUtc))
            .OrderByDescending(h => h.CreatedAtUtc)];
    }

    /// <summary>Holds the update for one target; refuses an unknown update or target, a second hold for it, a past end date or a bad reason.</summary>
    public async Task<Outcome> HoldAsync(Guid updateId, int revision, HoldTarget target, string? targetId, string? reason, DateTime? untilUtc, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is not null && (reason.Length > UpdateHoldEntity.MaxReasonLength || reason.Any(char.IsControl)))
        {
            return new Outcome($"The reason may have at most {UpdateHoldEntity.MaxReasonLength} characters on one line.");
        }

        if (untilUtc is { } until && until <= now)
        {
            return new Outcome("The end of the hold must lie in the future.");
        }

        var update = await db.Updates.AsNoTracking().SingleOrDefaultAsync(u => u.UpdateId == updateId && u.Revision == revision, cancellationToken).ConfigureAwait(false);
        if (update is null)
        {
            return new Outcome("The update is not in the catalog.");
        }

        var hold = new UpdateHoldEntity { UpdateId = updateId, Revision = revision, Reason = reason, UntilUtc = untilUtc, CreatedBy = actor.Length > 256 ? actor[..256] : actor, CreatedAtUtc = now };
        string? name = null;
        switch (target)
        {
            case HoldTarget.Ring when int.TryParse(targetId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ringId):
                name = await db.Rings.Where(r => r.Id == ringId).Select(r => r.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                hold.RingId = ringId;
                break;
            case HoldTarget.Group when int.TryParse(targetId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var groupId):
                name = await db.Groups.Where(g => g.Id == groupId).Select(g => g.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                hold.GroupId = groupId;
                break;
            case HoldTarget.Server when Guid.TryParse(targetId, out var clientId):
                name = await db.Clients.Where(c => c.Id == clientId).Select(c => c.HostName).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                hold.ClientId = clientId;
                break;
        }

        if (name is null)
        {
            return new Outcome("Choose the ring, group or server to hold the update for.");
        }

        var existing = await db.UpdateHolds
            .Where(h => h.UpdateId == updateId && h.Revision == revision && h.RingId == hold.RingId && h.GroupId == hold.GroupId && h.ClientId == hold.ClientId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing.Any(h => h.IsActive(now)))
        {
            return new Outcome($"The update is already on hold for {name}.");
        }

        // An ended hold of the same target gives way to the new one.
        db.UpdateHolds.RemoveRange(existing);
        db.UpdateHolds.Add(hold);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Update.Held", RolloutService.Describe(update.KbArticle, update.Title, updateId, revision),
            $"{target} {name}{(reason is null ? null : $": {reason}")}{(untilUtc is { } end ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $", until {end:yyyy-MM-dd HH:mm} UTC") : null)}", cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Lifts a hold; the withheld releases go out again.</summary>
    public async Task<Outcome> ResumeAsync(Guid updateId, int revision, int holdId, string actor, CancellationToken cancellationToken)
    {
        var hold = await db.UpdateHolds.SingleOrDefaultAsync(h => h.Id == holdId && h.UpdateId == updateId && h.Revision == revision, cancellationToken).ConfigureAwait(false);
        if (hold is null)
        {
            return new Outcome("The hold no longer exists.");
        }

        db.UpdateHolds.Remove(hold);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var update = await db.Updates.AsNoTracking().SingleOrDefaultAsync(u => u.UpdateId == updateId && u.Revision == revision, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Update.Resumed", RolloutService.Describe(update?.KbArticle, update?.Title ?? string.Empty, updateId, revision), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"hold {holdId}"), cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Deletes holds that ended; returns how many.</summary>
    public async Task<int> DeleteEndedAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.UpdateHolds.Where(h => h.UntilUtc != null && h.UntilUtc <= now).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
