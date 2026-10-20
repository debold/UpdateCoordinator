using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>Deployment state of one update on one server, as the dashboard shows it (docs/06-dashboard.md, section 3).</summary>
internal enum DeploymentState
{
    /// <summary>The server reported the update as missing, but it is not released for the server yet.</summary>
    Queued,

    /// <summary>Released and waiting for the server's maintenance window or installing.</summary>
    Pending,

    /// <summary>The last installation succeeded.</summary>
    Successful,

    /// <summary>The last installation failed.</summary>
    Failed,
}

/// <summary>One server × update pair.</summary>
internal sealed record DeploymentEntry(
    Guid ClientId,
    int? RingId,
    Guid UpdateId,
    int Revision,
    DeploymentState State,
    DateTime? EarliestInstallAtUtc,
    DateTime? LastResultAtUtc,
    int? HResult);

/// <summary>Counts per deployment state, e.g. for a ring or an update.</summary>
/// <param name="Queued">Reported as missing, not released yet.</param>
/// <param name="Pending">Released, not installed yet.</param>
/// <param name="Successful">Last installation succeeded.</param>
/// <param name="Failed">Last installation failed.</param>
/// <param name="NotNeeded">Servers that need no installation (installed otherwise or not applicable); only counted where a page knows the servers in scope.</param>
public sealed record DeploymentCounts(int Queued, int Pending, int Successful, int Failed, int NotNeeded = 0)
{
    public static DeploymentCounts Empty { get; } = new(0, 0, 0, 0);

    public int Total => Queued + Pending + Successful + Failed + NotNeeded;

    public static DeploymentCounts operator +(DeploymentCounts left, DeploymentCounts right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(left.Queued + right.Queued, left.Pending + right.Pending, left.Successful + right.Successful, left.Failed + right.Failed, left.NotNeeded + right.NotNeeded);
    }

    /// <summary>Counts of one update over <paramref name="servers"/> servers: every server without an entry needs no installation.</summary>
    internal static DeploymentCounts Of(IEnumerable<DeploymentEntry> entries, int servers)
    {
        var counts = Of(entries);
        return counts with { NotNeeded = Math.Max(0, servers - counts.Total) };
    }

    internal static DeploymentCounts Of(IEnumerable<DeploymentEntry> entries)
    {
        var list = entries as IReadOnlyCollection<DeploymentEntry> ?? [.. entries];
        return new DeploymentCounts(
            list.Count(e => e.State == DeploymentState.Queued),
            list.Count(e => e.State == DeploymentState.Pending),
            list.Count(e => e.State == DeploymentState.Successful),
            list.Count(e => e.State == DeploymentState.Failed));
    }
}

/// <summary>
/// Derives the deployment state of server × update pairs of approved servers from search results, releases and the
/// installation history. A pair is relevant when the server reported the update as missing, has it released or reported
/// an installation result for it. Computed in one place so overview, ring and server pages agree.
/// </summary>
internal sealed class DeploymentStatus(ServerDbContext db)
{
    /// <summary>Returns the pairs, optionally limited to one ring, one server or a set of updates.</summary>
    public async Task<IReadOnlyList<DeploymentEntry>> GetAsync(int? ringId, Guid? clientId, IReadOnlyCollection<Guid>? updateIds, CancellationToken cancellationToken)
    {
        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.State == RegistrationState.Approved)
            .Where(c => ringId == null || c.RingId == ringId)
            .Where(c => clientId == null || c.Id == clientId)
            .Select(c => new { c.Id, c.RingId })
            .ToDictionaryAsync(c => c.Id, c => c.RingId, cancellationToken)
            .ConfigureAwait(false);
        if (clients.Count == 0)
        {
            return [];
        }

        var ids = clients.Keys.ToList();
        var available = await db.ClientAvailableUpdates.AsNoTracking()
            .Where(a => ids.Contains(a.ClientId) && (updateIds == null || updateIds.Contains(a.UpdateId)))
            .Select(a => new { a.ClientId, a.UpdateId, a.Revision })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var releases = await db.Releases.AsNoTracking()
            .Where(r => ids.Contains(r.ClientId) && (updateIds == null || updateIds.Contains(r.UpdateId)))
            .Select(r => new { r.ClientId, r.UpdateId, r.Revision, r.EarliestInstallAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var installations = await db.Installations.AsNoTracking()
            .Where(i => ids.Contains(i.ClientId) && (updateIds == null || updateIds.Contains(i.UpdateId)))
            .Select(i => new { i.Id, i.ClientId, i.UpdateId, i.Revision, i.ResultCode, i.HResult, i.FinishedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var released = releases.ToDictionary(r => (r.ClientId, r.UpdateId, r.Revision), r => r.EarliestInstallAtUtc);
        var last = installations
            .GroupBy(i => (i.ClientId, i.UpdateId, i.Revision))
            .ToDictionary(g => g.Key, g => g.MaxBy(i => i.Id)!);

        var keys = available.Select(a => (a.ClientId, a.UpdateId, a.Revision))
            .Concat(released.Keys)
            .Concat(last.Keys)
            .Distinct();

        var entries = new List<DeploymentEntry>();
        foreach (var key in keys)
        {
            var hasRelease = released.TryGetValue(key, out var earliest);
            last.TryGetValue(key, out var result);
            var succeeded = result?.ResultCode is (int)InstallResultCode.Succeeded or (int)InstallResultCode.SucceededWithErrors;

            // A release after a failure stays Failed until a new result arrives; a success without a new search is final.
            var state = result is null
                ? hasRelease ? DeploymentState.Pending : DeploymentState.Queued
                : succeeded ? DeploymentState.Successful : DeploymentState.Failed;

            entries.Add(new DeploymentEntry(
                key.ClientId,
                clients[key.ClientId],
                key.UpdateId,
                key.Revision,
                state,
                hasRelease ? earliest : null,
                result?.FinishedAtUtc,
                result is null || succeeded ? null : result.HResult));
        }

        return entries;
    }
}

/// <summary>How deployment states are shown in the dashboard; shared by ring, update and server pages.</summary>
internal static class DeploymentText
{
    /// <summary>CSS class of the status pill.</summary>
    public static string PillClass(DeploymentState state) => state switch
    {
        DeploymentState.Successful => "ok",
        DeploymentState.Pending => "pend",
        DeploymentState.Queued => "queue",
        _ => "fail",
    };

    public static string Result(DeploymentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.State switch
        {
            DeploymentState.Successful => $"Installed {Format(entry.LastResultAtUtc)} UTC",
            DeploymentState.Failed => string.Create(CultureInfo.InvariantCulture, $"0x{entry.HResult ?? 0:X8}: {HResultCatalog.Describe(entry.HResult ?? 0)}"),
            DeploymentState.Pending => "Waiting for the maintenance window",
            _ => "Not released for the server's ring",
        };
    }

    public static string Format(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "–";
}
