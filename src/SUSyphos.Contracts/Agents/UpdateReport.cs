using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Result of an update run: pre-flight health checks and the updates available for this server
/// (see docs/05-agent-design.md, section 3). Sent only by approved clients.
/// </summary>
public sealed record UpdateReport
{
    /// <summary>Upper bound for available updates per report.</summary>
    public const int MaxAvailableUpdates = 5_000;

    /// <summary>Upper bound for health checks per report.</summary>
    public const int MaxHealthChecks = 32;

    /// <summary>When the run started (agent clock).</summary>
    [Required]
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>False if the search was not run (failed pre-flight check) or failed.</summary>
    public bool SearchSucceeded { get; init; }

    /// <summary>Windows Update error code if the search failed; translated into text by the central server.</summary>
    public int? SearchHResult { get; init; }

    [MaxLength(MaxHealthChecks)]
    public IReadOnlyList<HealthCheckResult> HealthChecks { get; init; } = [];

    /// <summary>Available updates; null if the search did not succeed.</summary>
    [MaxLength(MaxAvailableUpdates)]
    public IReadOnlyList<AvailableUpdate>? AvailableUpdates { get; init; }

    /// <summary>Upper bound for released updates reported as no longer offered.</summary>
    public const int MaxNotOffered = 500;

    /// <summary>
    /// Released updates that Windows Update neither offered in this search nor reports as installed (looked up by ID):
    /// withdrawn by Microsoft, or no longer applicable. Null if the search did not succeed or the agent does not check.
    /// </summary>
    [MaxLength(MaxNotOffered)]
    public IReadOnlyList<UpdateKey>? NotOffered { get; init; }
}

/// <summary>Identity of one update revision.</summary>
/// <param name="UpdateId">Windows Update ID.</param>
/// <param name="Revision">Revision number.</param>
public sealed record UpdateKey(Guid UpdateId, [property: Range(0, int.MaxValue)] int Revision);

[JsonConverter(typeof(JsonStringEnumConverter<HealthStatus>))]
public enum HealthStatus
{
    Ok = 0,

    /// <summary>Not blocking, but should be looked at (e.g. WSUS configured).</summary>
    Warning = 1,

    /// <summary>Blocking: no search or installation is run.</summary>
    Failed = 2,
}

/// <summary>
/// One pre-flight check, e.g. free disk space.
/// </summary>
public sealed record HealthCheckResult
{
    /// <summary>Check identifier, e.g. "DiskSpace".</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.RoleName)]
    public required string Name { get; init; }

    [EnumDataType(typeof(HealthStatus))]
    public required HealthStatus Status { get; init; }

    [StringLength(512)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public string? Detail { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<RebootBehavior>))]
public enum RebootBehavior
{
    NeverReboots = 0,
    AlwaysRequiresReboot = 1,
    CanRequestReboot = 2,
}

/// <summary>
/// An update available but not installed on the server.
/// </summary>
public sealed record AvailableUpdate : UpdateInfo
{
    /// <summary>Maximum download size in bytes as reported by Windows Update.</summary>
    [Range(0, 1_000_000_000_000)]
    public long? MaxDownloadSizeBytes { get; init; }

    [EnumDataType(typeof(RebootBehavior))]
    public RebootBehavior? RebootBehavior { get; init; }

    /// <summary>MSRC severity, e.g. "Critical"; only set for security updates.</summary>
    [StringLength(32)]
    [RegularExpression("^[A-Za-z]+$")]
    public string? MsrcSeverity { get; init; }

    /// <summary>Upper bound for superseded update IDs per update.</summary>
    public const int MaxSupersededUpdates = 500;

    /// <summary>Updates this update replaces, as Windows Update reports them (any revision); null if not reported.</summary>
    [MaxLength(MaxSupersededUpdates)]
    public IReadOnlyList<Guid>? SupersededUpdateIds { get; init; }
}

/// <summary>
/// Updates the central server has released for this server (see docs/05-agent-design.md, section 4).
/// </summary>
/// <param name="Version">Changes whenever the releases of the client change; announced in the heartbeat.</param>
/// <param name="Releases">Released updates with earliest installation time.</param>
/// <param name="MaintenanceWindows">Effective maintenance windows (server, group or system default); null lets the agent use its local configuration.</param>
/// <param name="RebootPolicy">Effective reboot policy (server, group or system default); null means the agent never reboots.</param>
/// <param name="RequiresLease">The server is in a coordinated group: it installs and reboots only with a lease.</param>
/// <param name="ManageAutomaticUpdates">Keep Windows from installing updates on its own: the agent sets "Configure Automatic Updates" to option 3 unless group policy controls it (docs/05-agent-design.md, section 3).</param>
/// <param name="UseMicrosoftUpdate">Search and install through Microsoft Update, so updates for other Microsoft products are included; the agent registers the service if needed (docs/05-agent-design.md, section 3).</param>
/// <param name="AgentSettings">Effective central agent settings by key (<see cref="Agents.AgentSettings"/>); settings not listed keep the agent's own value.</param>
public sealed record ReleaseList(long Version, IReadOnlyList<Release> Releases, IReadOnlyList<MaintenanceWindow>? MaintenanceWindows = null, RebootPolicy? RebootPolicy = null, bool RequiresLease = false, bool ManageAutomaticUpdates = false, bool UseMicrosoftUpdate = false, IReadOnlyDictionary<string, string>? AgentSettings = null);

/// <summary>
/// A released update and the earliest time it may be installed.
/// </summary>
/// <param name="UpdateId">The update.</param>
/// <param name="Revision">Its revision.</param>
/// <param name="EarliestInstallAt">Earliest installation time.</param>
/// <param name="OutsideWindows">The update never restarts and may be installed outside the maintenance windows (start rule).</param>
public sealed record Release(Guid UpdateId, int Revision, DateTimeOffset EarliestInstallAt, bool OutsideWindows = false);
