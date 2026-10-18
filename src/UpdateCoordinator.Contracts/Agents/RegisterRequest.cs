using System.ComponentModel.DataAnnotations;

namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Sent once by an agent that has never been connected. Contains only basic information;
/// the full inventory is a separate message.
/// </summary>
public sealed record RegisterRequest
{
    [Required]
    [StringLength(63, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.HostName)]
    public required string HostName { get; init; }

    /// <summary>DNS domain of the server; empty or null for workgroup servers.</summary>
    [StringLength(253)]
    [RegularExpression(ValidationPatterns.DomainName)]
    public string? DomainName { get; init; }

    [Required]
    [StringLength(23, MinimumLength = 3)]
    [RegularExpression(ValidationPatterns.OsVersion)]
    public required string OsVersion { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 5)]
    [RegularExpression(ValidationPatterns.SemanticVersion)]
    public required string AgentVersion { get; init; }
}
