using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Assignment;

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

    /// <summary>Most roles one rule can name; the rule matches servers with any of them.</summary>
    public const int MaxRuleRoles = 10;

    public Task<Outcome> CreateRuleAsync(
        int priority, AssignmentTarget target, int targetId, string? roles, string? domain, string? hostNamePattern, string actor, CancellationToken cancellationToken) =>
        SaveRuleAsync(null, priority, target, targetId, roles, domain, hostNamePattern, actor, cancellationToken);

    public Task<Outcome> UpdateRuleAsync(
        int ruleId, int priority, AssignmentTarget target, int targetId, string? roles, string? domain, string? hostNamePattern, string actor, CancellationToken cancellationToken) =>
        SaveRuleAsync(ruleId, priority, target, targetId, roles, domain, hostNamePattern, actor, cancellationToken);

    /// <summary>
    /// Gives rules that share a priority (allowed before 0.7.4) unique numbers once, keeping their order (priority, then age):
    /// the first keeps its number, the others move to the next free numbers after it. Returns how many were renumbered.
    /// </summary>
    public async Task<int> RenumberDuplicatePrioritiesAsync(CancellationToken cancellationToken)
    {
        var rules = await db.AssignmentRules.OrderBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var taken = rules.Select(r => r.Priority).ToHashSet();
        var seen = new HashSet<int>();
        var changed = 0;
        foreach (var rule in rules)
        {
            if (seen.Add(rule.Priority))
            {
                continue;
            }

            var candidate = rule.Priority + 1;
            while (taken.Contains(candidate) && candidate < Administration.RulePriority.Maximum)
            {
                candidate++;
            }

            if (taken.Contains(candidate))
            {
                continue;
            }

            taken.Add(candidate);
            seen.Add(candidate);
            rule.Priority = candidate;
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditLog.SystemActor, "AssignmentRule.PrioritiesRenumbered", null, $"{changed} rules with a duplicate priority got the next free number.", cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>Roles of a rule: comma-separated, any of them matches.</summary>
    public static IReadOnlyList<string> RuleRoles(string? roles) =>
        (roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<Outcome> SaveRuleAsync(
        int? ruleId, int priority, AssignmentTarget target, int targetId, string? roles, string? domain, string? hostNamePattern, string actor, CancellationToken cancellationToken)
    {
        var roleList = RuleRoles(roles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var role = roleList.Count == 0 ? null : string.Join(',', roleList);
        domain = Normalize(domain);
        hostNamePattern = Normalize(hostNamePattern);

        if (priority is < 0 or > 10_000)
        {
            return new Outcome("The priority must be between 0 and 10000.");
        }

        var others = await db.AssignmentRules.AsNoTracking().Where(r => r.Id != ruleId).Select(r => r.Priority).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (others.Contains(priority))
        {
            return new Outcome(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Another assignment rule already has priority {priority}; choose a free one, e.g. {Administration.RulePriority.NextFree(others)}."));
        }

        if (role is null && domain is null && hostNamePattern is null)
        {
            return new Outcome("A rule needs at least one condition (role, domain or host name pattern).");
        }

        if (roleList.Count > MaxRuleRoles || roleList.Any(r => !RoleRegex().IsMatch(r)))
        {
            return new Outcome($"Select at most {MaxRuleRoles} roles; each must look like a role name, e.g. DomainController.");
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

        AssignmentRuleEntity rule;
        if (ruleId is { } id)
        {
            var existing = await db.AssignmentRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return new Outcome("The rule no longer exists.");
            }

            rule = existing;
        }
        else
        {
            rule = new AssignmentRuleEntity();
            db.AssignmentRules.Add(rule);
        }

        rule.Priority = priority;
        rule.Target = target;
        rule.TargetId = targetId;
        rule.Role = role;
        rule.Domain = domain;
        rule.HostNamePattern = hostNamePattern;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = $"priority {priority}, {target} {targetId}, roles {role ?? "-"}, domain {domain ?? "-"}, host {hostNamePattern ?? "-"}";
        return await CompleteAsync(actor, ruleId is null ? "AssignmentRule.Created" : "AssignmentRule.Changed", target.ToString(), details, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> DeleteRuleAsync(int ruleId, string actor, CancellationToken cancellationToken)
    {
        var deleted = await db.AssignmentRules.Where(r => r.Id == ruleId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted == 0
            ? new Outcome("The rule no longer exists.")
            : await CompleteAsync(actor, "AssignmentRule.Deleted", ruleId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Assigns several servers to a ring manually (e.g. "Add servers" on the ring page); their groups and windows stay.</summary>
    public async Task<Outcome> MoveClientsToRingAsync(IReadOnlyCollection<Guid> clientIds, int ringId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientIds);
        if (clientIds.Count == 0)
        {
            return new Outcome("Select at least one server.");
        }

        var ring = await db.Rings.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ringId, cancellationToken).ConfigureAwait(false);
        if (ring is null)
        {
            return new Outcome("The ring no longer exists.");
        }

        var clients = await db.Clients.Where(c => clientIds.Contains(c.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (clients.Count == 0)
        {
            return new Outcome("The selected servers no longer exist.");
        }

        foreach (var client in clients)
        {
            client.ManualRingId = ringId;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var names = string.Join(", ", clients.Select(c => c.HostName).Order(StringComparer.OrdinalIgnoreCase).Take(20));
        return await CompleteAsync(actor, "Ring.ServersAdded", ring.Name, clients.Count > 20 ? $"{names} and {clients.Count - 20} more" : names, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> SetClientAssignmentAsync(        Guid clientId, int? manualRingId, IReadOnlyCollection<int> manualGroupIds, string? windowsText, string actor, CancellationToken cancellationToken)
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

    /// <summary>System default: whether agents keep Windows from installing updates on its own (docs/05-agent-design.md, section 3).</summary>
    public Task<Outcome> SetSystemManageAutomaticUpdatesAsync(bool manage, string actor, CancellationToken cancellationToken) =>
        SetSystemFlagAsync(SettingEntity.SystemManageAutomaticUpdates, manage, actor, "AutomaticUpdates.SystemDefaultChanged", manage ? "prevent automatic installation" : "leave unchanged", cancellationToken);

    /// <summary>System default: whether agents also search and install through Microsoft Update (other Microsoft products).</summary>
    public Task<Outcome> SetSystemMicrosoftUpdateAsync(bool use, string actor, CancellationToken cancellationToken) =>
        SetSystemFlagAsync(SettingEntity.SystemMicrosoftUpdate, use, actor, "MicrosoftUpdate.SystemDefaultChanged", use ? "Microsoft Update" : "Windows Update only", cancellationToken);

    private async Task<Outcome> SetSystemFlagAsync(string key, bool on, string actor, string auditAction, string text, CancellationToken cancellationToken)
    {
        var value = on ? "true" : "false";
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, auditAction, "system default", text, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the Microsoft Update handling of a group; null leaves it to other groups or the system default.</summary>
    public async Task<Outcome> SetGroupMicrosoftUpdateAsync(int groupId, bool? use, string actor, CancellationToken cancellationToken)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        if (group.UseMicrosoftUpdate == use)
        {
            return Outcome.Ok;
        }

        group.UseMicrosoftUpdate = use;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var text = use switch { true => "Microsoft Update", false => "Windows Update only", null => "inherited" };
        return await CompleteAsync(actor, "Group.MicrosoftUpdateChanged", group.Name, text, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the Automatic Updates handling of a group; null leaves it to other groups or the system default.</summary>
    public async Task<Outcome> SetGroupManageAutomaticUpdatesAsync(int groupId, bool? manage, string actor, CancellationToken cancellationToken)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        if (group.ManageAutomaticUpdates == manage)
        {
            return Outcome.Ok;
        }

        group.ManageAutomaticUpdates = manage;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var text = manage switch { true => "prevent automatic installation", false => "leave unchanged", null => "inherited" };
        return await CompleteAsync(actor, "Group.AutomaticUpdatesChanged", group.Name, text, null, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The central agent settings of the system default.</summary>
    public async Task<Dictionary<string, string>> GetSystemAgentSettingsAsync(CancellationToken cancellationToken) =>
        AgentSettingValues.Parse(await db.Settings.AsNoTracking().Where(s => s.Key == SettingEntity.SystemAgentSettings).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Replaces the system default of the central agent settings. Every entry must be a known setting with a valid value;
    /// an empty value removes the setting (agents use their own value).
    /// </summary>
    public async Task<Outcome> SetSystemAgentSettingsAsync(IReadOnlyDictionary<string, string?> values, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Validate(values) is { } error)
        {
            return new Outcome(error);
        }

        var json = AgentSettingValues.ToJson(Clean(values));
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingEntity.SystemAgentSettings, cancellationToken).ConfigureAwait(false);
        if (setting is null && json is not null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingEntity.SystemAgentSettings, Value = json });
        }
        else if (setting is not null && json is null)
        {
            db.Settings.Remove(setting);
        }
        else if (setting is not null)
        {
            setting.Value = json!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "AgentSettings.SystemDefaultChanged", "system", json ?? "none", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the central agent settings set on a group (values that differ from the system default); empty values are dropped.</summary>
    public async Task<Outcome> SetGroupAgentSettingsAsync(int groupId, IReadOnlyDictionary<string, string?> values, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Validate(values) is { } error)
        {
            return new Outcome(error);
        }

        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new Outcome("The group no longer exists.");
        }

        var json = AgentSettingValues.ToJson(Clean(values));
        if (json == group.AgentSettingsJson)
        {
            return Outcome.Ok;
        }

        group.AgentSettingsJson = json;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "AgentSettings.GroupChanged", group.Name, json ?? "none", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the central agent settings set on one server; empty values are dropped.</summary>
    public async Task<Outcome> SetClientAgentSettingsAsync(Guid clientId, IReadOnlyDictionary<string, string?> values, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Validate(values) is { } error)
        {
            return new Outcome(error);
        }

        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server no longer exists.");
        }

        var json = AgentSettingValues.ToJson(Clean(values));
        if (json == client.AgentSettingsJson)
        {
            return Outcome.Ok;
        }

        client.AgentSettingsJson = json;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "AgentSettings.ClientChanged", $"{client.HostName} ({clientId:D})", json ?? "none", clientId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets or (with null) removes one central agent setting of a group; the others stay as they are.</summary>
    public async Task<Outcome> SetGroupAgentSettingAsync(int groupId, string key, string? value, string actor, CancellationToken cancellationToken)
    {
        var json = await db.Groups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.AgentSettingsJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return await SetGroupAgentSettingsAsync(groupId, WithOne(json, key, value), actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets or (with null) removes one central agent setting of a server; the others stay as they are.</summary>
    public async Task<Outcome> SetClientAgentSettingAsync(Guid clientId, string key, string? value, string actor, CancellationToken cancellationToken)
    {
        var json = await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.AgentSettingsJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return await SetClientAgentSettingsAsync(clientId, WithOne(json, key, value), actor, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string?> WithOne(string? json, string key, string? value)
    {
        var values = AgentSettingValues.Parse(json).ToDictionary(v => v.Key, v => (string?)v.Value, StringComparer.Ordinal);
        values[key ?? string.Empty] = value;
        return values;
    }

    private static string? Validate(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
        {
            if (SUSyphos.Contracts.Agents.AgentSettings.Find(key) is not { } definition)
            {
                return "The agent setting is unknown.";
            }

            if (!string.IsNullOrWhiteSpace(value) && definition.Normalize(value) is null)
            {
                return Invalid(definition);
            }
        }

        return null;
    }

    private static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToDictionary(v => v.Key, v => v.Value!, StringComparer.Ordinal);

    private static string Invalid(SUSyphos.Contracts.Agents.AgentSettingDefinition definition) => definition.Kind switch
    {
        SUSyphos.Contracts.Agents.AgentSettingKind.Duration or SUSyphos.Contracts.Agents.AgentSettingKind.Number =>
            $"{definition.Label}: a whole number from {definition.Minimum} to {definition.Maximum}{(definition.Unit.Length > 0 ? " " + definition.Unit : string.Empty)}.",
        SUSyphos.Contracts.Agents.AgentSettingKind.Choice => $"{definition.Label}: one of {string.Join(", ", definition.Choices ?? [])}.",
        SUSyphos.Contracts.Agents.AgentSettingKind.Flag => $"{definition.Label}: on or off.",
        _ => $"{definition.Label}: at most {definition.Maximum} characters, no quotes or control characters.",
    };

    /// <summary>Sets the Automatic Updates and Microsoft Update settings of one server; null inherits from its groups or the system.</summary>
    public async Task<Outcome> SetClientUpdateSourceAsync(Guid clientId, bool? manageAutomaticUpdates, bool? useMicrosoftUpdate, string actor, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server no longer exists.");
        }

        if (client.ManageAutomaticUpdates == manageAutomaticUpdates && client.UseMicrosoftUpdate == useMicrosoftUpdate)
        {
            return Outcome.Ok;
        }

        client.ManageAutomaticUpdates = manageAutomaticUpdates;
        client.UseMicrosoftUpdate = useMicrosoftUpdate;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = $"automatic updates {(manageAutomaticUpdates is { } manage ? (manage ? "prevent" : "leave unchanged") : "inherited")}, Microsoft Update {(useMicrosoftUpdate is { } use ? (use ? "on" : "off") : "inherited")}";
        return await CompleteAsync(actor, "Client.UpdateSourceChanged", $"{client.HostName} ({clientId:D})", details, clientId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds servers to a group by hand (bulk action of the server list); memberships from rules become manual, other groups
    /// stay. Deleted servers and unknown ids are skipped. Returns how many servers were added.
    /// </summary>
    public async Task<(Outcome Outcome, int Added)> AddClientsToGroupAsync(IReadOnlyCollection<Guid> clientIds, int groupId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientIds);
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return (new Outcome("The selected group does not exist."), 0);
        }

        var ids = clientIds.Distinct().Take(500).ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id) && c.State != SUSyphos.Contracts.Agents.RegistrationState.Deleted).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (clients.Count == 0)
        {
            return (new Outcome("Select at least one server in the list first."), 0);
        }

        var memberships = await db.ClientGroups.Where(m => m.GroupId == groupId && clients.Contains(m.ClientId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var added = 0;
        foreach (var clientId in clients)
        {
            if (memberships.FirstOrDefault(m => m.ClientId == clientId) is { } membership)
            {
                membership.Manual = true;
            }
            else
            {
                db.ClientGroups.Add(new ClientGroupEntity { ClientId = clientId, GroupId = groupId, Manual = true });
                added++;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Group.ServersAdded", group.Name, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{clients.Count} servers ({added} new)"), cancellationToken).ConfigureAwait(false);
        foreach (var clientId in clients)
        {
            await assignment.RecalculateAsync(clientId, cancellationToken).ConfigureAwait(false);
        }

        return (Outcome.Ok, added);
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
