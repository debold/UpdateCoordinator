using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Assignment;

/// <summary>
/// Calculates ring, rule-based groups and effective maintenance windows of clients
/// (docs/04-central-server.md, sections 3 and 4):
/// ring = manual override, else first matching ring rule, else the fallback ring Production;
/// windows = server override, else the windows of its groups, else the system default; reboot policy likewise.
/// A change of the effective windows bumps the release version, so the agent fetches them; releases follow the ring.
/// </summary>
internal sealed class AssignmentService(ServerDbContext db, ReleasePlanner releases)
{
    /// <summary>Recalculates one client, or all clients if <paramref name="clientId"/> is null. Saves the changes.</summary>
    public async Task RecalculateAsync(Guid? clientId, CancellationToken cancellationToken)
    {
        var rules = await db.AssignmentRules.AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, cancellationToken).ConfigureAwait(false);
        var systemWindows = await GetSystemWindowsAsync(cancellationToken).ConfigureAwait(false);
        var systemReboot = await GetSystemRebootPolicyAsync(cancellationToken).ConfigureAwait(false);
        var systemManage = await GetSystemManageAutomaticUpdatesAsync(cancellationToken).ConfigureAwait(false);

        var clients = await db.Clients
            .Where(c => clientId == null || c.Id == clientId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = clients.Select(c => c.Id).ToList();
        var memberships = await db.ClientGroups.Where(m => ids.Contains(m.ClientId)).ToListAsync(cancellationToken).ConfigureAwait(false);

        // The final ring (highest order) is the fallback for servers without manual or rule-based ring.
        var fallback = await db.Rings.AsNoTracking().OrderByDescending(r => r.Order).Select(r => r.Id).FirstAsync(cancellationToken).ConfigureAwait(false);

        foreach (var client in clients)
        {
            client.RingId = client.ManualRingId
                ?? rules.FirstOrDefault(r => r.Target == AssignmentTarget.Ring && Matches(r, client))?.TargetId
                ?? fallback;

            // Rule-based memberships are recalculated; manual memberships stay untouched.
            var mine = memberships.Where(m => m.ClientId == client.Id).ToList();
            var ruleGroups = rules
                .Where(r => r.Target == AssignmentTarget.Group && groups.ContainsKey(r.TargetId) && Matches(r, client))
                .Select(r => r.TargetId)
                .ToHashSet();
            db.ClientGroups.RemoveRange(mine.Where(m => !m.Manual && !ruleGroups.Contains(m.GroupId)));
            db.ClientGroups.AddRange(ruleGroups
                .Where(g => !mine.Any(m => m.GroupId == g))
                .Select(g => new ClientGroupEntity { ClientId = client.Id, GroupId = g, Manual = false }));

            var groupIds = mine.Where(m => m.Manual).Select(m => m.GroupId).Concat(ruleGroups).Distinct().Order();
            var windows = MaintenanceWindowFormat.FromJson(client.MaintenanceWindowsJson)
                ?? Union(groupIds.Select(g => groups.TryGetValue(g, out var group) ? MaintenanceWindowFormat.FromJson(group.MaintenanceWindowsJson) : null))
                ?? systemWindows;

            // Reboots: the server setting wins; among groups the most restrictive one (Never) wins; else the system default.
            var groupPolicies = groupIds.Select(g => groups.TryGetValue(g, out var group) ? group.RebootPolicy : null).Where(p => p is not null).ToList();
            var reboot = client.RebootPolicy
                ?? (groupPolicies.Count == 0 ? (RebootPolicy?)null : groupPolicies.Contains(RebootPolicy.Never) ? RebootPolicy.Never : RebootPolicy.InsideMaintenanceWindow)
                ?? systemReboot;

            var json = MaintenanceWindowFormat.ToJson(windows);
            var requiresLease = groupIds.Any(g => groups.TryGetValue(g, out var group) && group.Coordinated);

            // Automatic Updates: the agent changes a server only if no group says otherwise ("leave unchanged" wins); else the system default.
            var groupManage = groupIds.Select(g => groups.TryGetValue(g, out var group) ? group.ManageAutomaticUpdates : null).Where(m => m is not null).ToList();
            var manage = groupManage.Count == 0 ? systemManage : groupManage.All(m => m == true);

            if (json != client.EffectiveWindowsJson || reboot != client.EffectiveRebootPolicy || requiresLease != client.RequiresLease || manage != client.EffectiveManageAutomaticUpdates)
            {
                client.EffectiveWindowsJson = json;
                client.EffectiveRebootPolicy = reboot;
                client.RequiresLease = requiresLease;
                client.EffectiveManageAutomaticUpdates = manage;
                client.ReleasesVersion++;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A ring change changes which releases apply to the client.
        await releases.SyncAsync(clientId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MaintenanceWindow>> GetSystemWindowsAsync(CancellationToken cancellationToken)
    {
        var stored = await db.Settings.AsNoTracking()
            .Where(s => s.Key == SettingEntity.SystemMaintenanceWindows)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return MaintenanceWindowFormat.FromJson(stored) ?? MaintenanceWindow.SystemDefault;
    }

    /// <summary>System default for Automatic Updates: off (only detect and warn) unless an administrator turned it on.</summary>
    public async Task<bool> GetSystemManageAutomaticUpdatesAsync(CancellationToken cancellationToken) =>
        await db.Settings.AsNoTracking()
            .Where(s => s.Key == SettingEntity.SystemManageAutomaticUpdates)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) == "true";

    /// <summary>System default reboot policy; Never unless an administrator changed it.</summary>
    public async Task<RebootPolicy> GetSystemRebootPolicyAsync(CancellationToken cancellationToken)
    {
        var stored = await db.Settings.AsNoTracking()
            .Where(s => s.Key == SettingEntity.SystemRebootPolicy)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return Enum.TryParse<RebootPolicy>(stored, out var policy) && Enum.IsDefined(policy) ? policy : RebootPolicy.Never;
    }

    internal static bool Matches(AssignmentRuleEntity rule, ClientEntity client)
    {
        if (rule.Role is null && rule.Domain is null && rule.HostNamePattern is null)
        {
            return false;
        }

        return (rule.Role is null || AssignmentAdministration.RuleRoles(rule.Role).Any(role => client.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)))
            && (rule.Domain is null || string.Equals(rule.Domain, client.DomainName, StringComparison.OrdinalIgnoreCase))
            && (rule.HostNamePattern is null || Wildcard.Matches(rule.HostNamePattern, client.HostName));
    }

    private static List<MaintenanceWindow>? Union(IEnumerable<IReadOnlyList<MaintenanceWindow>?> lists)
    {
        var windows = lists.Where(l => l is not null).SelectMany(l => l!).Take(MaintenanceWindow.MaxWindows).ToList();
        return windows.Count > 0 ? windows : null;
    }
}
