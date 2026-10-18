using System.ComponentModel.DataAnnotations;

namespace UpdateCoordinator.Contracts.Agents;

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
}
