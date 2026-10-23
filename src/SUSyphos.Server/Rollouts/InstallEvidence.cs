using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>
/// Whether an update is installed on a server, from every source the central server has: the inventory, the installation
/// results of SUSyphos and the Windows Update history (installations outside SUSyphos too). Drivers often disappear from
/// the Windows Update search once installed, so the search alone cannot tell (Marc, 2026-10-23).
/// </summary>
internal static class InstallEvidence
{
    /// <summary>"The update is already installed" (WU_E_ALREADY_INSTALLED as Windows reports it for drivers, 0x80070103).</summary>
    public const int AlreadyInstalled = unchecked((int)0x80070103);

    /// <summary>True for a result that leaves the update installed: success, success with errors or "already installed".</summary>
    public static bool LeavesInstalled(int resultCode, int hResult) =>
        resultCode is (int)InstallResultCode.Succeeded or (int)InstallResultCode.SucceededWithErrors || hResult == AlreadyInstalled;

    /// <summary>The (server, update) pairs among the given ones that are installed, regardless of the revision.</summary>
    public static async Task<HashSet<(Guid ClientId, Guid UpdateId)>> InstalledAsync(ServerDbContext db, IReadOnlyCollection<Guid> clientIds, IReadOnlyCollection<Guid> updateIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var installed = new HashSet<(Guid, Guid)>();
        if (clientIds.Count == 0 || updateIds.Count == 0)
        {
            return installed;
        }

        foreach (var pair in await db.ClientUpdates.AsNoTracking()
            .Where(c => clientIds.Contains(c.ClientId) && updateIds.Contains(c.UpdateId))
            .Select(c => new { c.ClientId, c.UpdateId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            installed.Add((pair.ClientId, pair.UpdateId));
        }

        // Otherwise the latest operation decides: an uninstallation after an installation means it is gone again.
        var operations = (await db.Installations.AsNoTracking()
                .Where(i => clientIds.Contains(i.ClientId) && updateIds.Contains(i.UpdateId))
                .Select(i => new { i.ClientId, i.UpdateId, At = i.FinishedAtUtc, i.ResultCode, i.HResult, Uninstall = false })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Concat(await db.ClientUpdateHistory.AsNoTracking()
                .Where(h => clientIds.Contains(h.ClientId) && updateIds.Contains(h.UpdateId))
                .Select(h => new { h.ClientId, h.UpdateId, At = h.DateUtc, ResultCode = (int)h.ResultCode, h.HResult, Uninstall = h.Operation == UpdateOperation.Uninstallation })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        foreach (var group in operations.GroupBy(o => (o.ClientId, o.UpdateId)))
        {
            var decisive = group
                .Where(o => o.Uninstall ? o.ResultCode is (int)InstallResultCode.Succeeded or (int)InstallResultCode.SucceededWithErrors : LeavesInstalled(o.ResultCode, o.HResult))
                .MaxBy(o => o.At);
            // The inventory stays the stronger evidence: it is what the server holds now.
            if (decisive is { Uninstall: false })
            {
                installed.Add(group.Key);
            }
        }

        return installed;
    }
}
