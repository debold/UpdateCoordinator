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
}
