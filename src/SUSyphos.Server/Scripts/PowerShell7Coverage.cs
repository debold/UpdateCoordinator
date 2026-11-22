using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Contracts.Scripts;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Scripts;

/// <summary>
/// Servers that run a PowerShell 7 script without PowerShell 7 (Marc, 2026-11-22, after Microsoft deprecated Windows
/// PowerShell 5.1): approved servers that have or are assigned a package with an approved PowerShell 7 script, whose agent
/// reports PowerShell 7 (from 0.9.0-rc.8) and found none. Such scripts run in Windows PowerShell 5.1 there.
/// </summary>
internal static class PowerShell7Coverage
{
    /// <summary>The first agent version that reports PowerShell 7 with its inventory.</summary>
    public const string ReportedSinceVersion = "0.9.0-rc.8";

    /// <summary>The WinGet package of PowerShell 7.</summary>
    public const string PackageId = "Microsoft.PowerShell";

    /// <summary>The servers concerned, optionally only for one package; empty while scripts are switched off.</summary>
    public static async Task<IReadOnlyList<(Guid ClientId, string HostName)>> MissingAsync(ServerDbContext db, string? packageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.Settings.AsNoTracking().Where(s => s.Key == ScriptAdministration.EnabledKey).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) != "true")
        {
            return [];
        }

        var packages = await (from s in db.PackageScripts.AsNoTracking()
                              join r in db.PackageScriptRevisions.AsNoTracking() on new { ScriptId = s.Id, Revision = s.ApprovedRevision ?? 0 } equals new { r.ScriptId, r.Revision }
                              where r.PowerShell == ScriptPowerShell.PowerShell7 && (packageId == null || s.PackageId == packageId)
                              select s.PackageId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (packages.Count == 0)
        {
            return [];
        }

        var installed = db.ClientSoftware.Where(c => c.PackageId != null && packages.Contains(c.PackageId)).Select(c => c.ClientId);
        var assigned = db.PackageAssignments.Where(a => packages.Contains(a.PackageId) && a.ClientId != null).Select(a => a.ClientId!.Value);
        var groups = db.PackageAssignments.Where(a => packages.Contains(a.PackageId) && a.GroupId != null).Select(a => a.GroupId!.Value);
        var allServers = await db.PackageAssignments.AnyAsync(a => packages.Contains(a.PackageId) && a.TargetKind == PackageAssignmentTarget.AllServers, cancellationToken).ConfigureAwait(false);
        var candidates = await db.Clients.AsNoTracking()
            .Where(c => c.State == RegistrationState.Approved && c.PowerShell7Version == null
                && (allServers || installed.Contains(c.Id) || assigned.Contains(c.Id) || db.ClientGroups.Any(g => g.ClientId == c.Id && groups.Contains(g.GroupId))))
            .Select(c => new { c.Id, c.HostName, c.AgentVersion })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. candidates.Where(c => Reports(c.AgentVersion)).OrderBy(c => c.HostName, StringComparer.OrdinalIgnoreCase).Select(c => (c.Id, c.HostName))];
    }

    /// <summary>Whether an agent of this version reports PowerShell 7 (older ones send nothing, which says nothing).</summary>
    public static bool Reports(string? agentVersion) =>
        AgentVersion.TryParse(agentVersion, out var running) && AgentVersion.TryParse(ReportedSinceVersion, out var since) && running >= since;
}
