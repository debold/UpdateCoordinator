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
}
