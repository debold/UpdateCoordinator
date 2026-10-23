using System.ComponentModel.DataAnnotations;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Periodic heartbeat of a registered agent. The client identity comes from the authentication header.
/// </summary>
public sealed record HeartbeatRequest
{
    [Required]
    [StringLength(128, MinimumLength = 5)]
    [RegularExpression(ValidationPatterns.SemanticVersion)]
    public required string AgentVersion { get; init; }

    /// <summary>True if the agent reset its trust in the central server locally (registry override) since the last acknowledged heartbeat.</summary>
    public bool TrustReset { get; init; }

    /// <summary>Current OS version including UBR; changes with every installed cumulative update.</summary>
    [StringLength(23, MinimumLength = 3)]
    [RegularExpression(ValidationPatterns.OsVersion)]
    public string? OsVersion { get; init; }

    /// <summary>Host name of the server; the central server uses it to detect copied machines.</summary>
    [StringLength(63, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.HostName)]
    public string? HostName { get; init; }

    /// <summary>True if Windows reports a pending reboot (component servicing or Windows Update), whatever caused it.</summary>
    public bool RebootPending { get; init; }

    /// <summary>Last start of the operating system; lets the central server see that a reboot happened.</summary>
    public DateTimeOffset? LastBootAt { get; init; }

    /// <summary>Why the last self-update failed; null when it succeeded or none was attempted (docs/05-agent-design.md, section 7).</summary>
    [StringLength(512)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public string? AgentUpdateError { get; init; }

    /// <summary>Why the agent did not move to the announced new central server address; null when no move is pending or it has not tried yet.</summary>
    [StringLength(512)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public string? ServerMoveError { get; init; }

    /// <summary>
    /// Every central agent setting as the agent applies it, by setting key; sent when it changed since the last
    /// acknowledged heartbeat (and after each start), otherwise null. The central server checks keys and values itself.
    /// </summary>
    public IReadOnlyDictionary<string, AppliedAgentSetting>? AppliedSettings { get; init; }

    /// <summary>Current activity (live status); null when the agent is idle.</summary>
    public AgentActivity? Activity { get; init; }

    /// <summary>Why a pending reboot waits, or that the agent restarts the server; null when no reboot is pending or not decided yet.</summary>
    public RebootWait? RebootWait { get; init; }

    /// <summary>An administrator requested a restart in the maintenance window and the agent holds it.</summary>
    public bool RestartRequested { get; init; }

    /// <summary>When the agent plans the requested restart (start of the next suitable window); null if it cannot tell.</summary>
    public DateTimeOffset? PlannedRestartAt { get; init; }

    /// <summary>When the agent plans its next update search; null before the first one (then with the next cycle).</summary>
    public DateTimeOffset? NextSearchAt { get; init; }

    /// <summary>When the agent plans its next inventory; null before the first one (then with the next cycle).</summary>
    public DateTimeOffset? NextInventoryAt { get; init; }
}
