using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Assignment;

/// <summary>What a one-time window applies to.</summary>
internal enum WindowScope
{
    All,
    Ring,
    Group,
    Server,
}

/// <summary>A one-time window as the dashboard lists it.</summary>
internal sealed record OneTimeWindowView(int Id, WindowScope Scope, string ScopeName, DateTime StartUtc, DateTime EndUtc, string? Reason, string CreatedBy);

/// <summary>
/// One-time maintenance windows (Marc, 2026-10-27, docs/04-central-server.md): a date and time span for all servers, a
/// ring, a group or a server, valid for every released update in addition to the weekly windows. Agents get the windows
/// of their server with the release list; adding or removing one announces a new release version to the servers concerned.
/// </summary>
internal sealed class OneTimeWindows(ServerDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    /// <summary>How far ahead agents get one-time windows.</summary>
    public static readonly TimeSpan DeliveredAhead = TimeSpan.FromDays(60);

    /// <summary>The windows that have not ended more than <see cref="OneTimeWindowEntity.KeepAfterEnd"/> ago, soonest first.</summary>
    public async Task<List<OneTimeWindowView>> ListAsync(CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime - OneTimeWindowEntity.KeepAfterEnd;
        var windows = await db.OneTimeWindows.AsNoTracking().Where(w => w.EndUtc > since).OrderBy(w => w.StartUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rings = await db.Rings.AsNoTracking().ToDictionaryAsync(r => r.Id, r => $"{r.Order} {r.Name}", cancellationToken).ConfigureAwait(false);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken).ConfigureAwait(false);
        var clientIds = windows.Where(w => w.ClientId is not null).Select(w => w.ClientId!.Value).ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.HostName, cancellationToken).ConfigureAwait(false);
        return [.. windows.Select(w =>
            w.RingId is { } ring ? new OneTimeWindowView(w.Id, WindowScope.Ring, rings.GetValueOrDefault(ring, "?"), w.StartUtc, w.EndUtc, w.Reason, w.CreatedBy)
            : w.GroupId is { } group ? new OneTimeWindowView(w.Id, WindowScope.Group, groups.GetValueOrDefault(group, "?"), w.StartUtc, w.EndUtc, w.Reason, w.CreatedBy)
            : w.ClientId is { } client ? new OneTimeWindowView(w.Id, WindowScope.Server, clients.GetValueOrDefault(client, "?"), w.StartUtc, w.EndUtc, w.Reason, w.CreatedBy)
            : new OneTimeWindowView(w.Id, WindowScope.All, "All servers", w.StartUtc, w.EndUtc, w.Reason, w.CreatedBy))];
    }

    /// <summary>Adds a window; refuses an unknown target, a start in the past, an end not after the start, more than a week, or a bad reason.</summary>
    public async Task<Outcome> AddAsync(WindowScope scope, string? targetId, DateTime startUtc, DateTime endUtc, string? reason, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is not null && (reason.Length > OneTimeWindowEntity.MaxReasonLength || reason.Any(char.IsControl)))
        {
            return new Outcome($"The reason may have at most {OneTimeWindowEntity.MaxReasonLength} characters on one line.");
        }

        if (endUtc <= startUtc)
        {
            return new Outcome("The window must end after it starts.");
        }

        if (endUtc - startUtc > OneTimeWindowEntity.MaxLength)
        {
            return new Outcome($"A one-time window lasts at most {OneTimeWindowEntity.MaxLength.TotalDays} days.");
        }

        if (startUtc < now.AddMinutes(-5))
        {
            return new Outcome("The window must not start in the past.");
        }

        var window = new OneTimeWindowEntity { StartUtc = startUtc, EndUtc = endUtc, Reason = reason, CreatedBy = actor.Length > 256 ? actor[..256] : actor, CreatedAtUtc = now };
        string? name = scope == WindowScope.All ? "all servers" : null;
        switch (scope)
        {
            case WindowScope.Ring when int.TryParse(targetId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ringId):
                name = await db.Rings.Where(r => r.Id == ringId).Select(r => "ring " + r.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                window.RingId = ringId;
                break;
            case WindowScope.Group when int.TryParse(targetId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var groupId):
                name = await db.Groups.Where(g => g.Id == groupId).Select(g => "group " + g.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                window.GroupId = groupId;
                break;
            case WindowScope.Server when Guid.TryParse(targetId, out var clientId):
                name = await db.Clients.Where(c => c.Id == clientId).Select(c => "server " + c.HostName).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                window.ClientId = clientId;
                break;
        }

        if (name is null)
        {
            return new Outcome("Choose all servers, a ring, a group or a server for the window.");
        }

        db.OneTimeWindows.Add(window);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(window, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "OneTimeWindow.Added", name, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{startUtc:yyyy-MM-dd HH:mm}–{endUtc:yyyy-MM-dd HH:mm} UTC{(reason is null ? null : $": {reason}")}"), cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> RemoveAsync(int id, string actor, CancellationToken cancellationToken)
    {
        var window = await db.OneTimeWindows.SingleOrDefaultAsync(w => w.Id == id, cancellationToken).ConfigureAwait(false);
        if (window is null)
        {
            return new Outcome("The window no longer exists.");
        }

        db.OneTimeWindows.Remove(window);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(window, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "OneTimeWindow.Removed", id.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{window.StartUtc:yyyy-MM-dd HH:mm}–{window.EndUtc:yyyy-MM-dd HH:mm} UTC"), cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>The windows of a server that have not ended and start within <see cref="DeliveredAhead"/>: global, its ring, its groups, itself.</summary>
    public static async Task<List<OneTimeWindow>> ForClientAsync(ServerDbContext db, Guid clientId, int? ringId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var groups = db.ClientGroups.Where(m => m.ClientId == clientId).Select(m => m.GroupId);
        var until = nowUtc + DeliveredAhead;
        var windows = await db.OneTimeWindows.AsNoTracking()
            .Where(w => w.EndUtc > nowUtc && w.StartUtc < until)
            .Where(w => (w.RingId == null && w.GroupId == null && w.ClientId == null) || w.RingId == ringId || w.ClientId == clientId || (w.GroupId != null && groups.Contains(w.GroupId.Value)))
            .OrderBy(w => w.StartUtc)
            .Take(OneTimeWindow.MaxWindows)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. windows.Select(w => new OneTimeWindow(new DateTimeOffset(DateTime.SpecifyKind(w.StartUtc, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(w.EndUtc, DateTimeKind.Utc))))];
    }

    /// <summary>Deletes windows that ended more than <see cref="OneTimeWindowEntity.KeepAfterEnd"/> ago; returns how many.</summary>
    public async Task<int> DeleteOldAsync(CancellationToken cancellationToken)
    {
        var before = timeProvider.GetUtcNow().UtcDateTime - OneTimeWindowEntity.KeepAfterEnd;
        return await db.OneTimeWindows.Where(w => w.EndUtc < before).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The servers of the window fetch their release list again (a new version), so they get the change.</summary>
    private Task<int> AnnounceAsync(OneTimeWindowEntity window, CancellationToken cancellationToken)
    {
        var clients = db.Clients.AsQueryable();
        if (window.RingId is { } ring)
        {
            clients = clients.Where(c => c.RingId == ring);
        }
        else if (window.GroupId is { } group)
        {
            clients = clients.Where(c => db.ClientGroups.Any(m => m.ClientId == c.Id && m.GroupId == group));
        }
        else if (window.ClientId is { } client)
        {
            clients = clients.Where(c => c.Id == client);
        }

        return clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.ReleasesVersion, c => c.ReleasesVersion + 1), cancellationToken);
    }
}
