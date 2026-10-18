using System.ComponentModel.DataAnnotations;

namespace UpdateCoordinator.Agent;

/// <summary>
/// Connection settings for the central server. Bound from the "CentralServer" configuration section.
/// </summary>
internal sealed class CentralServerOptions : IValidatableObject
{
    public const string SectionName = "CentralServer";

    /// <summary>Base address of the central server, e.g. https://updates.example.local:8443/. Must use HTTPS.</summary>
    [Required]
    public Uri? BaseAddress { get; set; }

    /// <summary>Interval between heartbeats while the central server is reachable.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for the back-off delay while the central server is unreachable.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (BaseAddress is { } address && (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult("The central server address must be an absolute HTTPS URI.", [nameof(BaseAddress)]);
        }

        if (MaxBackoff < HeartbeatInterval)
        {
            yield return new ValidationResult("MaxBackoff must not be shorter than HeartbeatInterval.", [nameof(MaxBackoff)]);
        }
    }
}
