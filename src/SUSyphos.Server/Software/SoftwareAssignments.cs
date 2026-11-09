using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Software;

/// <summary>
/// Assignments of a package to groups and servers for new installations (Marc, 2026-11-08; Admins only): the server gets
/// the package when its version is released for the server's ring. Removing an assignment uninstalls nothing unless the
/// assignment says so ("uninstall when removed": the servers it covered uninstall the package in their next maintenance
/// window, unless another assignment still covers them).
/// </summary>
internal sealed class SoftwareAssignments(ServerDbContext db, ReleasePlanner releases, AuditLog audit, Agents.AgentActionService actions, TimeProvider timeProvider)
{
    /// <summary>Adds an assignment to exactly one group or server, with an optional fixed version of the catalog.</summary>
    public async Task<Outcome> AddAsync(string packageId, int? groupId, Guid? clientId, string? fixedVersion, bool uninstallOnRemoval, string actor, CancellationToken cancellationToken)
    {
        if (!await SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("Software management is switched off.");
        }

        if (groupId is null == clientId is null)
        {
            return new Outcome("Choose one group or one server.");
        }

        var package = await db.Packages.AsNoTracking().SingleOrDefaultAsync(p => p.PackageId == packageId, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return new Outcome("The package is not in the catalog.");
        }

        fixedVersion = string.IsNullOrWhiteSpace(fixedVersion) ? null : fixedVersion.Trim();
        if (fixedVersion is not null && !await db.PackageVersions.AnyAsync(v => v.PackageId == package.PackageId && v.Version == fixedVersion, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("The version is not known in the catalog.");
        }

        string target;
        if (groupId is { } group)
        {
            target = await db.Groups.Where(g => g.Id == group).Select(g => "group " + g.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
        else
        {
            target = await db.Clients.Where(c => c.Id == clientId).Select(c => "server " + c.HostName).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }

        if (target.Length == 0)
        {
            return new Outcome("The group or server does not exist.");
        }

        if (await db.PackageAssignments.AnyAsync(a => a.PackageId == package.PackageId && a.GroupId == groupId && a.ClientId == clientId, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome($"The package is already assigned to the {target}.");
        }

        db.PackageAssignments.Add(new PackageAssignmentEntity
        {
            PackageId = package.PackageId,
            TargetKind = groupId is null ? PackageAssignmentTarget.Server : PackageAssignmentTarget.Group,
            GroupId = groupId,
            ClientId = clientId,
            Mode = PackageAssignmentMode.Install,
            FixedVersion = fixedVersion,
            UninstallOnRemoval = uninstallOnRemoval,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = actor,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Package.Assigned", package.PackageId, string.Create(CultureInfo.InvariantCulture, $"{target}, version {fixedVersion ?? "newest"}, uninstall when removed {(uninstallOnRemoval ? "yes" : "no")}"), cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Removes an assignment; the package stays installed unless "uninstall when removed" was set.</summary>
    public async Task<Outcome> RemoveAsync(int assignmentId, string actor, CancellationToken cancellationToken)
    {
        var assignment = await db.PackageAssignments.SingleOrDefaultAsync(a => a.Id == assignmentId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return new Outcome("The assignment no longer exists.");
        }

        var covered = assignment.UninstallOnRemoval ? await ServersAsync(assignment, cancellationToken).ConfigureAwait(false) : [];
        db.PackageAssignments.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (covered.Count > 0 && await SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false))
        {
            // Servers another assignment of the package still covers keep it.
            var stillAssigned = new HashSet<Guid>();
            foreach (var other in await db.PackageAssignments.AsNoTracking().Where(a => a.PackageId == assignment.PackageId && a.Mode == PackageAssignmentMode.Install).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                stillAssigned.UnionWith(await ServersAsync(other, cancellationToken).ConfigureAwait(false));
            }

            foreach (var server in covered.Where(s => !stillAssigned.Contains(s)))
            {
                // A server without the package installed is skipped by the queue itself.
                await actions.QueueSoftwareUninstallAsync(server, assignment.PackageId, actor, cancellationToken).ConfigureAwait(false);
            }
        }
        await audit.WriteAsync(actor, "Package.AssignmentRemoved", assignment.PackageId, string.Create(CultureInfo.InvariantCulture, $"{(assignment.GroupId is { } group ? $"group {group}" : $"server {assignment.ClientId}")}"), cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>The servers an assignment covers: the server, or the members of the group.</summary>
    private async Task<List<Guid>> ServersAsync(PackageAssignmentEntity assignment, CancellationToken cancellationToken) =>
        assignment.ClientId is { } server
            ? [server]
            : await db.ClientGroups.AsNoTracking().Where(m => m.GroupId == assignment.GroupId).Select(m => m.ClientId).ToListAsync(cancellationToken).ConfigureAwait(false);
}
