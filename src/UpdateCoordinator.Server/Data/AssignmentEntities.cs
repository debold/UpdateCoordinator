namespace UpdateCoordinator.Server.Data;

/// <summary>
/// A deployment ring (docs/04-central-server.md, section 3). Rings are fixed: Pilot, Test, Production and the Default ring.
/// </summary>
internal sealed class RingEntity
{
    public const int PilotId = 1;
    public const int TestId = 2;
    public const int ProductionId = 3;
    public const int DefaultId = 4;

    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Deployment order; lower rings receive updates first. The default ring is deployed last.</summary>
    public int Order { get; set; }

    public bool IsDefault { get; set; }
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

    /// <summary>Detected role reported in the inventory, e.g. DomainController.</summary>
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

    public required string Key { get; set; }

    public required string Value { get; set; }
}
