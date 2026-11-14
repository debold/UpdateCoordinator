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

    /// <summary>What removing an assignment would uninstall: servers it covers that have the package, and those that keep it through another assignment.</summary>
    /// <param name="Uninstall">Servers that would uninstall the package (they have it and no other assignment covers them).</param>
    /// <param name="KeptByOther">Servers with the package that another assignment of it still covers.</param>
    public sealed record RemovalPreview(int Uninstall, int KeptByOther);

    public async Task<RemovalPreview?> PreviewRemovalAsync(int assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await db.PackageAssignments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assignmentId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return null;
        }

        var (uninstall, kept) = await SplitAsync(assignment, cancellationToken).ConfigureAwait(false);
        return new RemovalPreview(uninstall.Count, kept);
    }

    /// <summary>
    /// Removes an assignment. The servers it covered uninstall the package in their next software window when
    /// <paramref name="uninstall"/> says so, or, when it is null, when the assignment was created with "uninstall when
    /// removed" (Marc, 2026-11-14: the removal dialog shows the saved choice and lets it be changed).
    /// </summary>
    public async Task<Outcome> RemoveAsync(int assignmentId, string actor, CancellationToken cancellationToken, bool? uninstall = null)
    {
        var assignment = await db.PackageAssignments.SingleOrDefaultAsync(a => a.Id == assignmentId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return new Outcome("The assignment no longer exists.");
        }

        var remove = uninstall ?? assignment.UninstallOnRemoval;
        var (targets, _) = remove ? await SplitAsync(assignment, cancellationToken).ConfigureAwait(false) : ([], 0);
        db.PackageAssignments.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var queued = 0;
        if (targets.Count > 0 && await SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false))
        {
            var batch = Guid.NewGuid();
            foreach (var server in targets)
            {
                if ((await actions.QueueSoftwareUninstallAsync(server, assignment.PackageId, SUSyphos.Contracts.Agents.AgentActionKind.UninstallInWindow, actor, cancellationToken, batch).ConfigureAwait(false)).Succeeded)
                {
                    queued++;
                }
            }
        }

        await audit.WriteAsync(actor, "Package.AssignmentRemoved", assignment.PackageId, string.Create(CultureInfo.InvariantCulture, $"{(assignment.GroupId is { } group ? $"group {group}" : $"server {assignment.ClientId}")}, {(remove ? $"uninstall from {queued} servers" : "kept installed")}"), cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>The servers of an assignment that have the package: those no other assignment covers, and how many another one keeps.</summary>
    private async Task<(List<Guid> Uninstall, int KeptByOther)> SplitAsync(PackageAssignmentEntity assignment, CancellationToken cancellationToken)
    {
        var covered = await ServersAsync(assignment, cancellationToken).ConfigureAwait(false);
        var installed = (await db.ClientSoftware.AsNoTracking().Where(s => s.PackageId == assignment.PackageId && covered.Contains(s.ClientId)).Select(s => s.ClientId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var stillAssigned = new HashSet<Guid>();
        foreach (var other in await db.PackageAssignments.AsNoTracking().Where(a => a.PackageId == assignment.PackageId && a.Id != assignment.Id && a.Mode == PackageAssignmentMode.Install).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            stillAssigned.UnionWith(await ServersAsync(other, cancellationToken).ConfigureAwait(false));
        }

        var withPackage = covered.Where(installed.Contains).Distinct().ToList();
        return ([.. withPackage.Where(s => !stillAssigned.Contains(s))], withPackage.Count(stillAssigned.Contains));
    }

    /// <summary>Ends every assignment of a package without uninstalling (the uninstallation from servers queues its own).</summary>
    public async Task<int> EndAllAsync(string packageId, string actor, CancellationToken cancellationToken)
    {
        var assignments = await db.PackageAssignments.Where(a => a.PackageId == packageId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (assignments.Count == 0)
        {
            return 0;
        }

        db.PackageAssignments.RemoveRange(assignments);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Package.AssignmentsEnded", packageId, string.Create(CultureInfo.InvariantCulture, $"{assignments.Count} assignments"), cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return assignments.Count;
    }

    /// <summary>The servers an assignment covers: the server, or the members of the group.</summary>
    private async Task<List<Guid>> ServersAsync(PackageAssignmentEntity assignment, CancellationToken cancellationToken) =>
        assignment.ClientId is { } server
            ? [server]
            : await db.ClientGroups.AsNoTracking().Where(m => m.GroupId == assignment.GroupId).Select(m => m.ClientId).ToListAsync(cancellationToken).ConfigureAwait(false);
}
