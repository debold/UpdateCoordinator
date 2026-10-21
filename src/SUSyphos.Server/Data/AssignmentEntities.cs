namespace SUSyphos.Server.Data;

/// <summary>
/// A deployment ring (docs/04-central-server.md, section 3). Rings are fixed: Pilot, Test and Production. Production is the final ring and the fallback for servers without a ring assignment.
/// </summary>
internal sealed class RingEntity
{
    public const int PilotId = 1;
    public const int TestId = 2;
    public const int ProductionId = 3;


    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Deployment order; lower rings receive updates first. The fallback ring (Production) is deployed last.</summary>
    public int Order { get; set; }

    /// <summary>Marks the fallback ring.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Installations in this ring start spread over this many hours after the release: each server gets a fixed offset
    /// derived from server and update, so not all servers install at once. 0 = no spread.
    /// </summary>
    public int SpreadHours { get; set; }

    public const int MaxSpreadHours = 168;
}

/// <summary>
/// A group of servers, e.g. domain controllers, with optional own maintenance windows.
/// </summary>
internal sealed class GroupEntity
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Maintenance windows of the group as JSON; null uses the system default.</summary>
    public string? MaintenanceWindowsJson { get; set; }

    /// <summary>Reboot policy of the group; null leaves it to other groups or the system default.</summary>
    public SUSyphos.Contracts.Agents.RebootPolicy? RebootPolicy { get; set; }

    /// <summary>Whether agents in the group keep Windows from installing updates on its own; null leaves it to other groups or the system default.</summary>
    public bool? ManageAutomaticUpdates { get; set; }

    /// <summary>Whether agents in the group use Microsoft Update (other Microsoft products); null leaves it to other groups or the system default.</summary>
    public bool? UseMicrosoftUpdate { get; set; }

    /// <summary>Members install and reboot only with a lease, so not all of them are down at the same time.</summary>
    public bool Coordinated { get; set; }

    /// <summary>Share of members that may hold a lease at the same time, in percent.</summary>
    public int LeasePercent { get; set; } = 25;

    /// <summary>Upper bound of members holding a lease at the same time.</summary>
    public int LeaseMaximum { get; set; } = 5;

    /// <summary>Failed or expired leases in a row; a success resets it.</summary>
    public int ConsecutiveLeaseFailures { get; set; }

    /// <summary>Set when too many leases failed in a row: no leases until an operator resumes the group.</summary>
    public bool LeasesHalted { get; set; }
}

/// <summary>
/// Membership of a client in a group, either assigned manually or by a rule.
/// </summary>
internal sealed class ClientGroupEntity
{
    public Guid ClientId { get; set; }

    public int GroupId { get; set; }

    public bool Manual { get; set; }
}

internal enum AssignmentTarget
{
    Ring = 0,
    Group = 1,
}

/// <summary>
/// Rule for automatic ring or group assignment. All set conditions must match; at least one is required.
/// For rings the rule with the lowest priority number wins; group rules all apply.
/// </summary>
internal sealed class AssignmentRuleEntity
{
    public int Id { get; set; }

    public int Priority { get; set; }

    public AssignmentTarget Target { get; set; }

    public int TargetId { get; set; }

    /// <summary>Roles reported in the inventory, comma-separated (e.g. DomainController,SqlServer); any of them matches.</summary>
    public string? Role { get; set; }

    /// <summary>DNS domain of the server.</summary>
    public string? Domain { get; set; }

    /// <summary>Host name pattern with * and ? wildcards, e.g. "SQL*".</summary>
    public string? HostNamePattern { get; set; }
}

/// <summary>
/// Global settings that administrators change at runtime (key/value).
/// </summary>
internal sealed class SettingEntity
{
    public const string SystemMaintenanceWindows = "MaintenanceWindows.SystemDefault";

    public const string SystemRebootPolicy = "Reboot.SystemDefault";

    public const string SystemManageAutomaticUpdates = "AutomaticUpdates.SystemDefault";

    public const string SystemMicrosoftUpdate = "MicrosoftUpdate.SystemDefault";

    /// <summary>Set once the default start rule for definition updates was created, so a deleted rule stays deleted.</summary>
    public const string DefinitionRuleCreated = "StartRules.DefinitionRuleCreated";

    public required string Key { get; set; }

    public required string Value { get; set; }
}
