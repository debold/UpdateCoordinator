using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>Ring IDs of a start rule, stored comma-separated.</summary>
internal static class StartRings
{
    public static IReadOnlyList<int> Parse(string? value) =>
        [.. (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()];

    public static string? Format(IEnumerable<int> ids)
    {
        var list = ids.Distinct().Order().ToList();
        return list.Count == 0 ? null : string.Join(',', list.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// Creating, renaming and removing rings (docs/04-central-server.md, rings). The last ring is always the final ring and
/// the fallback for servers without assignment, so it can neither be removed nor have a ring after it; new rings are added
/// before it. A ring with servers cannot be removed.
/// </summary>
internal sealed partial class RingAdministration(ServerDbContext db, AssignmentService assignment, AuditLog audit)
{
    public const int MaxRings = 10;

    /// <summary>Default rule for the transition into a newly added ring's successor.</summary>
    private static readonly (int MinDays, int MaxDays, int Feedback, int Success) DefaultTransition = (2, 7, 80, 95);

    public async Task<Outcome> CreateAsync(string? name, string actor, CancellationToken cancellationToken)
    {
        name = name?.Trim();
        if (!IsName(name))
        {
            return new Outcome("The ring name must have 1 to 32 letters, digits, spaces, '.', '_' or '-'.");
        }

        var rings = await db.Rings.OrderBy(r => r.Order).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rings.Count >= MaxRings)
        {
            return new Outcome($"At most {MaxRings} rings are possible.");
        }

        if (rings.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return new Outcome("A ring with this name already exists.");
        }

        // The new ring goes before the final ring: the previous ring now leads into it, and it leads into the final ring.
        var final = rings[^1];
        var ring = new RingEntity { Id = rings.Max(r => r.Id) + 1, Name = name!, Order = final.Order };
        final.Order++;
        db.Rings.Add(ring);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.RingTransitions.Add(new RingTransitionEntity
        {
            FromRingId = ring.Id,
            MinimumWait = TimeSpan.FromDays(DefaultTransition.MinDays),
            MaximumWait = TimeSpan.FromDays(DefaultTransition.MaxDays),
            MinimumFeedbackPercent = DefaultTransition.Feedback,
            MinimumSuccessPercent = DefaultTransition.Success,
        });

        // Running rollouts get the new ring: released if the final ring already has the update, else waiting for its turn.
        var finalStates = await db.RolloutRings.Where(r => r.RingId == final.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.RolloutRings.AddRange(finalStates.Select(s => new RolloutRingEntity
        {
            UpdateId = s.UpdateId,
            Revision = s.Revision,
            RingId = ring.Id,
            State = s.State == RingRolloutState.Released ? RingRolloutState.Released : RingRolloutState.Waiting,
            ReleasedAtUtc = s.State == RingRolloutState.Released ? s.ReleasedAtUtc : null,
            StateChangedAtUtc = s.StateChangedAtUtc,
        }));
        MarkFinal(rings.Append(ring));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Ring.Created", ring.Name, $"before {final.Name}", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> RenameAsync(int ringId, string? name, string actor, CancellationToken cancellationToken)
    {
        name = name?.Trim();
        if (!IsName(name))
        {
            return new Outcome("The ring name must have 1 to 32 letters, digits, spaces, '.', '_' or '-'.");
        }

        var ring = await db.Rings.SingleOrDefaultAsync(r => r.Id == ringId, cancellationToken).ConfigureAwait(false);
        if (ring is null)
        {
            return new Outcome("The ring no longer exists.");
        }

        if (ring.Name == name)
        {
            return Outcome.Ok;
        }

        if (await db.Rings.AnyAsync(r => r.Id != ringId && r.Name == name, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("A ring with this name already exists.");
        }

        var old = ring.Name;
        ring.Name = name!;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Ring.Renamed", name, $"was {old}", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Sets the spread of a ring; it applies to releases created from now on.</summary>
    public async Task<Outcome> SetSpreadAsync(int ringId, int hours, string actor, CancellationToken cancellationToken)
    {
        if (hours is < 0 or > RingEntity.MaxSpreadHours)
        {
            return new Outcome($"The spread must lie between 0 and {RingEntity.MaxSpreadHours} hours.");
        }

        var ring = await db.Rings.SingleOrDefaultAsync(r => r.Id == ringId, cancellationToken).ConfigureAwait(false);
        if (ring is null)
        {
            return new Outcome("The ring no longer exists.");
        }

        if (ring.SpreadHours == hours)
        {
            return Outcome.Ok;
        }

        ring.SpreadHours = hours;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Ring.SpreadChanged", ring.Name, $"{hours} hours", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> DeleteAsync(int ringId, string actor, CancellationToken cancellationToken)
    {
        var rings = await db.Rings.OrderBy(r => r.Order).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ring = rings.FirstOrDefault(r => r.Id == ringId);
        if (ring is null)
        {
            return new Outcome("The ring no longer exists.");
        }

        if (ring == rings[^1])
        {
            return new Outcome("The final ring cannot be removed: it is the fallback for servers without assignment.");
        }

        if (await db.Clients.AnyAsync(c => c.RingId == ringId || c.ManualRingId == ringId, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("The ring still has servers. Move them to another ring first (assignment rules or the server's assignment).");
        }

        if (await db.AssignmentRules.AnyAsync(r => r.Target == AssignmentTarget.Ring && r.TargetId == ringId, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("Assignment rules still assign servers to this ring. Change or delete them first.");
        }

        // Start rules forget the ring; its transition and rollout states go with it.
        foreach (var rule in await db.StartRules.Where(r => r.StartRingIds != null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            rule.StartRingIds = StartRings.Format(StartRings.Parse(rule.StartRingIds).Where(id => id != ringId));
        }

        await db.RingTransitions.Where(t => t.FromRingId == ringId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Rings.Remove(ring);
        var remaining = rings.Where(r => r != ring).ToList();
        for (var i = 0; i < remaining.Count; i++)
        {
            remaining[i].Order = i + 1;
        }

        MarkFinal(remaining);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Ring.Deleted", ring.Name, null, cancellationToken).ConfigureAwait(false);
        await assignment.RecalculateAsync(null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private static void MarkFinal(IEnumerable<RingEntity> rings)
    {
        var ordered = rings.OrderBy(r => r.Order).ToList();
        foreach (var ring in ordered)
        {
            ring.IsDefault = ring == ordered[^1];
        }
    }

    private static bool IsName(string? name) => name is not null && NameRegex().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,31}$")]
    private static partial Regex NameRegex();
}
