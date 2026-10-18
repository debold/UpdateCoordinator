using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Result of one installation run (batch) on a server (see docs/05-agent-design.md, section 5).
/// </summary>
public sealed record InstallReport
{
    /// <summary>Upper bound for results per report.</summary>
    public const int MaxResults = 500;

    /// <summary>Unique per run, so a report buffered locally and sent twice is stored only once.</summary>
    [Required]
    public required Guid RunId { get; init; }

    [Required]
    public required DateTimeOffset StartedAt { get; init; }

    [Required]
    public required DateTimeOffset FinishedAt { get; init; }

    [MinLength(1)]
    [MaxLength(MaxResults)]
    public required IReadOnlyList<InstallResult> Results { get; init; }

    /// <summary>True if at least one update needs a reboot to complete (the agent never reboots on its own).</summary>
    public bool RebootRequired { get; init; }

    /// <summary>OS version including UBR after the run.</summary>
    [StringLength(23, MinimumLength = 3)]
    [RegularExpression(ValidationPatterns.OsVersion)]
    public string? OsVersionAfter { get; init; }

    /// <summary>Health checks after the run (counterpart to the pre-flight check).</summary>
    [MaxLength(UpdateReport.MaxHealthChecks)]
    public IReadOnlyList<HealthCheckResult> PostInstallChecks { get; init; } = [];
}

/// <summary>
/// WUA operation result code (OperationResultCode).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InstallResultCode>))]
public enum InstallResultCode
{
    NotStarted = 0,
    InProgress = 1,
    Succeeded = 2,
    SucceededWithErrors = 3,
    Failed = 4,
    Aborted = 5,
}

/// <summary>
/// Outcome for one update of an installation run.
/// </summary>
public sealed record InstallResult
{
    [Required]
    public required Guid UpdateId { get; init; }

    [Range(0, int.MaxValue)]
    public required int Revision { get; init; }

    [EnumDataType(typeof(InstallResultCode))]
    public required InstallResultCode ResultCode { get; init; }

    /// <summary>Windows Update error code (0 on success); translated into text by the central server.</summary>
    public int HResult { get; init; }

    public bool RebootRequired { get; init; }
}
