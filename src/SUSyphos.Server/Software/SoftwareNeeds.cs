using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>A software version a server needs or has, as an entry of the update catalog.</summary>
internal readonly record struct SoftwareNeed(Guid ClientId, Guid UpdateId, int Revision, DateTime ReportedAtUtc);

/// <summary>
/// What servers need of the software catalog (docs/04-central-server.md, 7c; no table of its own): every known version
/// of an installed package that is newer than the installed one, and for a package assigned to the server or one of its
/// groups and not installed, the fixed version of the assignment or else the newest known version. A server "has" the
/// version it reports. The release planner and the deployment state treat these like the missing and installed Windows
/// updates an agent reports. Nothing is needed while software management is switched off, on servers that reported
/// that WinGet is not supported there, or of a version with a virus finding.
/// </summary>
internal static class SoftwareNeeds
{
    /// <summary>The needed and the installed software versions of the servers (all servers when <paramref name="clientIds"/> is null).</summary>
    public static async Task<(IReadOnlyList<SoftwareNeed> Needed, IReadOnlyList<SoftwareNeed> Installed)> LoadAsync(ServerDbContext db, IReadOnlyCollection<Guid>? clientIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!await SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return ([], []);
        }

        var unsupported = db.Clients.Where(c => c.SoftwareState == SoftwareSupportState.NotSupported).Select(c => c.Id);
        var programs = await db.ClientSoftware.AsNoTracking()
            .Where(s => s.PackageId != null && (clientIds == null || clientIds.Contains(s.ClientId)) && !unsupported.Contains(s.ClientId))
            .Select(s => new { s.ClientId, s.PackageId, s.InstalledVersion, s.ReportedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var assigned = await AssignedAsync(db, clientIds, cancellationToken).ConfigureAwait(false);
        if (programs.Count == 0 && assigned.Count == 0)
        {
            return ([], []);
        }

        var packageIds = programs.Select(p => p.PackageId!).Concat(assigned.Select(a => a.PackageId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var versions = (await db.PackageVersions.AsNoTracking()
                .Where(v => packageIds.Contains(v.PackageId) && v.ScanState != InstallerScanState.Detected)
                .Select(v => new { v.PackageId, v.Version, v.UpdateId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(v => v.PackageId, StringComparer.OrdinalIgnoreCase);

        var needed = new List<SoftwareNeed>();
        var installed = new List<SoftwareNeed>();
        foreach (var program in programs.Where(p => p.InstalledVersion is not null))
        {
            foreach (var version in versions[program.PackageId!])
            {
                var order = VersionComparer.Instance.Compare(version.Version, program.InstalledVersion);
                if (order > 0)
                {
                    needed.Add(new SoftwareNeed(program.ClientId, version.UpdateId, SoftwarePackages.SoftwareRevision, program.ReportedAtUtc));
                }
                else if (order == 0)
                {
                    installed.Add(new SoftwareNeed(program.ClientId, version.UpdateId, SoftwarePackages.SoftwareRevision, program.ReportedAtUtc));
                }
            }
        }

        // Assigned and not installed (new installations): the fixed version, else the newest known one.
        var present = programs.Select(p => (p.ClientId, p.PackageId!.ToUpperInvariant())).ToHashSet();
        foreach (var assignment in assigned.Where(a => !present.Contains((a.ClientId, a.PackageId.ToUpperInvariant()))))
        {
            var target = assignment.FixedVersion is { } fixedVersion
                ? versions[assignment.PackageId].FirstOrDefault(v => string.Equals(v.Version, fixedVersion, StringComparison.OrdinalIgnoreCase))
                : versions[assignment.PackageId].MaxBy(v => v.Version, VersionComparer.Instance);
            if (target is not null)
            {
                needed.Add(new SoftwareNeed(assignment.ClientId, target.UpdateId, SoftwarePackages.SoftwareRevision, assignment.CreatedAtUtc));
            }
        }

        return (needed.Distinct().ToList(), installed.Distinct().ToList());
    }

    /// <summary>Install assignments resolved to servers: directly or through a group, without servers that cannot run WinGet.</summary>
    private static async Task<List<(Guid ClientId, string PackageId, string? FixedVersion, DateTime CreatedAtUtc)>> AssignedAsync(ServerDbContext db, IReadOnlyCollection<Guid>? clientIds, CancellationToken cancellationToken)
    {
        var assignments = await db.PackageAssignments.AsNoTracking().Where(a => a.Mode == PackageAssignmentMode.Install).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (assignments.Count == 0)
        {
            return [];
        }

        var groupIds = assignments.Where(a => a.GroupId is not null).Select(a => a.GroupId!.Value).Distinct().ToList();
        var members = (await db.ClientGroups.AsNoTracking().Where(m => groupIds.Contains(m.GroupId)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(m => m.GroupId, m => m.ClientId);
        var unsupported = (await db.Clients.AsNoTracking().Where(c => c.SoftwareState == SoftwareSupportState.NotSupported).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var resolved = new List<(Guid, string, string?, DateTime)>();
        foreach (var assignment in assignments)
        {
            IEnumerable<Guid> servers = assignment.ClientId is { } server ? [server] : members[assignment.GroupId!.Value];
            resolved.AddRange(servers
                .Where(s => (clientIds is null || clientIds.Contains(s)) && !unsupported.Contains(s))
                .Select(s => (s, assignment.PackageId, assignment.FixedVersion, assignment.CreatedAtUtc)));
        }

        // A server assigned the same package twice (directly and through a group) takes the assignment with a fixed version.
        return [.. resolved.GroupBy(r => (r.Item1, r.Item2.ToUpperInvariant())).Select(g => g.OrderBy(r => r.Item3 is null).First())];
    }
}
