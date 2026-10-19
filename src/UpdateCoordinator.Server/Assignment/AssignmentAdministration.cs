using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Assignment;

/// <summary>
/// Administrative changes to groups, assignment rules, maintenance windows and reboot policies. Every change is validated,
/// written to the audit log and followed by a recalculation of the affected clients.
/// </summary>
internal sealed partial class AssignmentAdministration(ServerDbContext db, AssignmentService assignment, AuditLog audit)
{
    public async Task<Outcome> SetSystemWindowsAsync(string? text, string actor, CancellationToken cancellationToken)
    {
        if (!MaintenanceWindowFormat.TryParse(text, out var windows, out var error))
        {
            return new Outcome(error);
        }

        if (windows.Count == 0)
        {
            return new Outcome("The system default needs at least one maintenance window.");
        }

        var json = MaintenanceWindowFormat.ToJson(windows);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingEntity.SystemMaintenanceWindows, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingEntity.SystemMaintenanceWindows, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "MaintenanceWindows.SystemDefaultChanged", "system default", MaintenanceWindowFormat.Format(windows), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> CreateGroupAsync(string? name, string? description, string? windowsText, string actor, CancellationToken cancellationToken)
    {
        if (!IsName(name))
        {
            return new Outcome("The group name must have 1 to 64 letters, digits, spaces, '.', '_' or '-'.");
        }

        if (description is { Length: > 256 })
        {
            return new Outcome("The description may have at most 256 characters.");
        }

        if (!MaintenanceWindowFormat.TryParse(windowsText, out var windows, out var error))
        {
            return new Outcome(error);
        }

        if (await db.Groups.AnyAsync(g => g.Name == name, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("A group with this name already exists.");
        }

        db.Groups.Add(new GroupEntity
        {
            Name = name!,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            MaintenanceWindowsJson = windows.Count > 0 ? MaintenanceWindowFormat.ToJson(windows) : null,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Group.Created", name, MaintenanceWindowFormat.Format(windows), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> SetGroupWindowsAsync(int groupId, string? windowsText, string actor, CancellationToken cancellationToken)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        if (!MaintenanceWindowFormat.TryParse(windowsText, out var windows, out var error))
        {
            return new Outcome(error);
        }

        group.MaintenanceWindowsJson = windows.Count > 0 ? MaintenanceWindowFormat.ToJson(windows) : null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Group.WindowsChanged", group.Name, MaintenanceWindowFormat.Format(windows), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> SetGroupDescriptionAsync(int groupId, string? description, string actor, CancellationToken cancellationToken)
    {
        if (description is { Length: > 256 })
        {
            return new Outcome("The description may have at most 256 characters.");
        }

        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        var value = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (group.Description == value)
        {
            return Outcome.Ok;
        }

        group.Description = value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Group.DescriptionChanged", group.Name, value, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> DeleteGroupAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        // Rules pointing to the group are removed with it; memberships are removed by the database.
        await db.AssignmentRules.Where(r => r.Target == AssignmentTarget.Group && r.TargetId == groupId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Groups.Remove(group);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Group.Deleted", group.Name, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> CreateRuleAsync(
        int priority, AssignmentTarget target, int targetId, string? role, string? domain, string? hostNamePattern, string actor, CancellationToken cancellationToken)
    {
        role = Normalize(role);
        domain = Normalize(domain);
        hostNamePattern = Normalize(hostNamePattern);

        if (priority is < 0 or > 10_000)
        {
            return new Outcome("The priority must be between 0 and 10000.");
        }

        if (role is null && domain is null && hostNamePattern is null)
        {
            return new Outcome("A rule needs at least one condition (role, domain or host name pattern).");
        }

        if (role is not null && !RoleRegex().IsMatch(role))
        {
            return new Outcome("The role must look like a role name, e.g. DomainController.");
        }

        if (domain is not null && (domain.Length > 253 || !DomainRegex().IsMatch(domain)))
        {
            return new Outcome("The domain must be a DNS domain name.");
        }

        if (hostNamePattern is not null && !PatternRegex().IsMatch(hostNamePattern))
        {
            return new Outcome("The host name pattern may contain letters, digits, '-', '*' and '?' (at most 63 characters).");
        }

        var targetExists = target switch
        {
            AssignmentTarget.Ring => await db.Rings.AnyAsync(r => r.Id == targetId, cancellationToken).ConfigureAwait(false),
            AssignmentTarget.Group => await db.Groups.AnyAsync(g => g.Id == targetId, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
        if (!targetExists)
        {
            return new Outcome("The selected ring or group does not exist.");
        }

        db.AssignmentRules.Add(new AssignmentRuleEntity
        {
            Priority = priority,
            Target = target,
            TargetId = targetId,
            Role = role,
            Domain = domain,
            HostNamePattern = hostNamePattern,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = $"priority {priority}, {target} {targetId}, role {role ?? "-"}, domain {domain ?? "-"}, host {hostNamePattern ?? "-"}";
        return await CompleteAsync(actor, "AssignmentRule.Created", target.ToString(), details, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> DeleteRuleAsync(int ruleId, string actor, CancellationToken cancellationToken)
    {
        var deleted = await db.AssignmentRules.Where(r => r.Id == ruleId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted == 0
            ? new Outcome("The rule no longer exists.")
            : await CompleteAsync(actor, "AssignmentRule.Deleted", ruleId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> SetClientAssignmentAsync(
        Guid clientId, int? manualRingId, IReadOnlyCollection<int> manualGroupIds, string? windowsText, string actor, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server no longer exists.");
        }

        if (manualRingId is { } ring && !await db.Rings.AnyAsync(r => r.Id == ring, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("The selected ring does not exist.");
        }

        var groups = manualGroupIds.Distinct().ToList();
        if (groups.Count > 0 && await db.Groups.CountAsync(g => groups.Contains(g.Id), cancellationToken).ConfigureAwait(false) != groups.Count)
        {
            return new Outcome("A selected group does not exist.");
        }

        if (!MaintenanceWindowFormat.TryParse(windowsText, out var windows, out var error))
        {
            return new Outcome(error);
        }

        client.ManualRingId = manualRingId;
        client.MaintenanceWindowsJson = windows.Count > 0 ? MaintenanceWindowFormat.ToJson(windows) : null;

        var manual = await db.ClientGroups.Where(m => m.ClientId == clientId && m.Manual).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.ClientGroups.RemoveRange(manual.Where(m => !groups.Contains(m.GroupId)));
        var existing = await db.ClientGroups.Where(m => m.ClientId == clientId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var groupId in groups)
        {
            var membership = existing.FirstOrDefault(m => m.GroupId == groupId);
            if (membership is null)
            {
                db.ClientGroups.Add(new ClientGroupEntity { ClientId = clientId, GroupId = groupId, Manual = true });
            }
            else
            {
                membership.Manual = true;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = $"ring {(manualRingId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "automatic")}, groups {string.Join(",", groups)}, windows {(windows.Count > 0 ? MaintenanceWindowFormat.Format(windows) : "inherited")}";
        return await CompleteAsync(actor, "Client.AssignmentChanged", $"{client.HostName} ({clientId:D})", details, clientId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> SetSystemRebootPolicyAsync(RebootPolicy policy, string actor, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(policy))
        {
            return new Outcome("The reboot policy is invalid.");
        }

        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingEntity.SystemRebootPolicy, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingEntity.SystemRebootPolicy, Value = policy.ToString() });
        }
        else
        {
            setting.Value = policy.ToString();
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Reboot.SystemDefaultChanged", "system default", policy.ToString(), null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the reboot policy of a group; null leaves it to other groups or the system default.</summary>
    public async Task<Outcome> SetGroupRebootPolicyAsync(int groupId, RebootPolicy? policy, string actor, CancellationToken cancellationToken)
    {
        if (policy is { } value && !Enum.IsDefined(value))
        {
            return new Outcome("The reboot policy is invalid.");
        }

        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        group.RebootPolicy = policy;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Group.RebootPolicyChanged", group.Name, policy?.ToString() ?? "inherited", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns lease coordination of a group on or off and sets how many members may hold a lease at once.</summary>
    public async Task<Outcome> SetGroupCoordinationAsync(int groupId, bool coordinated, int percent, int maximum, string actor, CancellationToken cancellationToken)
    {
        if (percent is < 1 or > 100 || maximum is < 1 or > 1_000)
        {
            return new Outcome("The share must be 1 to 100 percent and the maximum 1 to 1000 servers.");
        }

        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        if (group.Coordinated == coordinated && group.LeasePercent == percent && group.LeaseMaximum == maximum)
        {
            return Outcome.Ok;
        }

        group.Coordinated = coordinated;
        group.LeasePercent = percent;
        group.LeaseMaximum = maximum;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(coordinated ? "coordinated" : "not coordinated")}, {percent}%, at most {maximum}");
        return await CompleteAsync(actor, "Group.CoordinationChanged", group.Name, details, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the reboot policy of a server; null uses its groups or the system default.</summary>
    public async Task<Outcome> SetClientRebootPolicyAsync(Guid clientId, RebootPolicy? policy, string actor, CancellationToken cancellationToken)
    {
        if (policy is { } value && !Enum.IsDefined(value))
        {
            return new Outcome("The reboot policy is invalid.");
        }

        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server no longer exists.");
        }

        if (client.RebootPolicy == policy)
        {
            return Outcome.Ok;
        }

        client.RebootPolicy = policy;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Client.RebootPolicyChanged", $"{client.HostName} ({clientId:D})", policy?.ToString() ?? "inherited", clientId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Outcome> CompleteAsync(string actor, string action, string? target, string? details, Guid? clientId, CancellationToken cancellationToken)
    {
        await audit.WriteAsync(actor, action, target, details, cancellationToken).ConfigureAwait(false);
        await assignment.RecalculateAsync(clientId, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsName(string? name) => name is not null && NameRegex().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,63}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(ValidationPatterns.RoleName)]
    private static partial Regex RoleRegex();

    [GeneratedRegex(ValidationPatterns.DomainName)]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"^[A-Za-z0-9*?\-]{1,63}$")]
    private static partial Regex PatternRegex();
}
