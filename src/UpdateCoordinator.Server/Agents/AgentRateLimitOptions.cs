using System.ComponentModel.DataAnnotations;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Limits for the anonymous registration endpoint. Bound from the "Agents:RateLimit" configuration section.
/// </summary>
internal sealed class AgentRateLimitOptions
{
    public const string SectionName = "Agents:RateLimit";

    /// <summary>Registrations allowed per remote IP address within <see cref="Window"/>.</summary>
    [Range(1, 10_000)]
    public int RegisterPermitLimit { get; set; } = 10;

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
