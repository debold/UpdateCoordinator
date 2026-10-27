using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Assignment;

/// <summary>What a stretch of a day allows.</summary>
public enum SlotKind
{
    /// <summary>No window: nothing starts.</summary>
    Closed,

    /// <summary>A weekly maintenance window.</summary>
    Open,

    /// <summary>A one-time maintenance window.</summary>
    OneTime,

    /// <summary>A blackout period: nothing starts, whatever the windows say.</summary>
    Blocked,
}

/// <summary>A stretch of one day from minute <paramref name="From"/> to minute <paramref name="To"/> (at most 1440), with what it allows and why.</summary>
public sealed record Slot(int From, int To, SlotKind Kind, string Source);

public sealed record DayPreview(DateOnly Day, IReadOnlyList<Slot> Slots);

/// <summary>The preview of a level: its days, the holds that apply there and a note on time zones.</summary>
public sealed record SchedulePreviewResult(string Title, IReadOnlyList<DayPreview> Days, IReadOnlyList<string> Holds);

/// <summary>
/// When installations may start on the next days (Marc, 2026-10-27): weekly windows, one-time windows and blackout
/// periods of a level combined, shown on every level (system, ring, group, server) as one bar per day. One calculation for
/// all levels; times in the time zone of the central server.
/// </summary>
internal sealed class SchedulePreview(ServerDbContext db, AssignmentService assignment, BlackoutPeriods blackouts, TimeProvider timeProvider)
{
    /// <summary>Days the preview shows.</summary>
    public const int Days = 30;

    /// <summary>Resolution of the bars in minutes.</summary>
    public const int Step = 15;

    /// <summary>Combines the windows of a level into days; a blackout wins over every window, a one-time window over a weekly one.</summary>
    public static List<DayPreview> Build(
        IReadOnlyList<MaintenanceWindow> weekly,
        string weeklySource,
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc, string Source)> oneTime,
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc, string Source)> blocked,
        DateOnly from,
        int days,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(weekly);
        ArgumentNullException.ThrowIfNull(oneTime);
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(zone);
        var result = new List<DayPreview>(days);
        for (var d = 0; d < days; d++)
        {
            var day = from.AddDays(d);
            var slots = new List<Slot>();
            for (var minute = 0; minute < 1440; minute += Step)
            {
                var local = day.ToDateTime(TimeOnly.MinValue).AddMinutes(minute);
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
                var (kind, source) = blocked.FirstOrDefault(b => b.StartUtc <= utc && utc < b.EndUtc) is { Source: { } blocking }
                    ? (SlotKind.Blocked, blocking)
                    : oneTime.FirstOrDefault(w => w.StartUtc <= utc && utc < w.EndUtc) is { Source: { } once }
                        ? (SlotKind.OneTime, once)
                        : MaintenanceSchedule.IsOpen(weekly, local) ? (SlotKind.Open, weeklySource) : (SlotKind.Closed, string.Empty);
                if (slots.Count > 0 && slots[^1].Kind == kind && slots[^1].Source == source)
                {
                    slots[^1] = slots[^1] with { To = minute + Step };
                }
                else
                {
                    slots.Add(new Slot(minute, minute + Step, kind, source));
                }
            }

            result.Add(new DayPreview(day, slots));
        }

        return result;
    }

    public async Task<SchedulePreviewResult> ForSystemAsync(CancellationToken cancellationToken)
    {
        var windows = await assignment.GetSystemWindowsAsync(cancellationToken).ConfigureAwait(false);
        return await BuildAsync("All servers (system default)", windows, "system default", w => w.RingId == null && w.GroupId == null && w.ClientId == null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchedulePreviewResult?> ForRingAsync(int ringId, CancellationToken cancellationToken)
    {
        var ring = await db.Rings.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ringId, cancellationToken).ConfigureAwait(false);
        if (ring is null)
        {
            return null;
        }

        var windows = await assignment.GetSystemWindowsAsync(cancellationToken).ConfigureAwait(false);
        return await BuildAsync($"Ring {ring.Order} {ring.Name}", windows, "system default", w => (w.RingId == null && w.GroupId == null && w.ClientId == null) || w.RingId == ringId, h => h.RingId == ringId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchedulePreviewResult?> ForGroupAsync(int groupId, CancellationToken cancellationToken)
    {
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var own = MaintenanceWindowFormat.FromJson(group.MaintenanceWindowsJson);
        var windows = own ?? await assignment.GetSystemWindowsAsync(cancellationToken).ConfigureAwait(false);
        return await BuildAsync($"Group {group.Name}", windows, own is null ? "system default" : $"group {group.Name}", w => (w.RingId == null && w.GroupId == null && w.ClientId == null) || w.GroupId == groupId, h => h.GroupId == groupId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchedulePreviewResult?> ForClientAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return null;
        }

        var groups = await db.ClientGroups.AsNoTracking().Where(m => m.ClientId == clientId).Select(m => m.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var windows = MaintenanceWindowFormat.FromJson(client.EffectiveWindowsJson) ?? await assignment.GetSystemWindowsAsync(cancellationToken).ConfigureAwait(false);
        return await BuildAsync(
            client.HostName,
            windows,
            "effective windows of the server",
            w => (w.RingId == null && w.GroupId == null && w.ClientId == null) || w.RingId == client.RingId || w.ClientId == clientId || (w.GroupId is { } g && groups.Contains(g)),
            h => h.RingId == client.RingId || h.ClientId == clientId || (h.GroupId is { } g && groups.Contains(g)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SchedulePreviewResult> BuildAsync(
        string title,
        IReadOnlyList<MaintenanceWindow> weekly,
        string weeklySource,
        Func<OneTimeWindowEntity, bool> oneTimeApplies,
        Func<UpdateHoldEntity, bool>? holdApplies,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var until = now.AddDays(Days + 1);
        var oneTime = (await db.OneTimeWindows.AsNoTracking().Where(w => w.EndUtc > now && w.StartUtc < until).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(oneTimeApplies)
            .Select(w => (w.StartUtc, w.EndUtc, w.Reason is null ? "one-time window" : $"one-time window: {w.Reason}"))
            .ToList();
        var blocked = (await blackouts.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(b => b.EndUtc > now)
            .Select(b => (b.StartUtc, b.EndUtc, $"blackout: {b.Reason}"))
            .ToList();
        var today = DateOnly.FromDateTime(Components.DisplayTime.ToLocal(now));
        var days = Build(weekly, weeklySource, oneTime, blocked, today, Days, Components.DisplayTime.Zone);

        var holds = new List<string>();
        if (holdApplies is not null)
        {
            var active = (await Rollouts.UpdateHolds.ActiveAsync(db, now, cancellationToken).ConfigureAwait(false)).Where(holdApplies).ToList();
            var ids = active.Select(h => h.UpdateId).ToList();
            var names = await db.Updates.AsNoTracking().Where(u => ids.Contains(u.UpdateId)).Select(u => new { u.UpdateId, u.Revision, u.KbArticle, u.Title }).ToListAsync(cancellationToken).ConfigureAwait(false);
            holds.AddRange(active.Select(h => names.FirstOrDefault(n => n.UpdateId == h.UpdateId && n.Revision == h.Revision) is { } n
                ? $"{(n.KbArticle is null ? n.Title : $"KB{n.KbArticle}")}{(h.UntilUtc is { } end ? $" until {Components.DisplayTime.Format(end)}" : null)}"
                : h.UpdateId.ToString("D")));
        }

        return new SchedulePreviewResult(title, days, holds);
    }
}
