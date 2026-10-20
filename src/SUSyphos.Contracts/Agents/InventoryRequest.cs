using System.ComponentModel.DataAnnotations;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Full inventory of a server (see docs/05-agent-design.md, section 2). Sent after registration,
/// on service start, when the content changes and at least once a day.
/// </summary>
public sealed record InventoryRequest
{
    /// <summary>Upper bound for installed updates per message.</summary>
    public const int MaxInstalledUpdates = 5_000;

    /// <summary>Upper bound for detected roles per message.</summary>
    public const int MaxRoles = 32;

    [Required]
    [StringLength(63, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.HostName)]
    public required string HostName { get; init; }

    [StringLength(253)]
    [RegularExpression(ValidationPatterns.DomainName)]
    public string? DomainName { get; init; }

    [Required]
    public required OperatingSystemInfo OperatingSystem { get; init; }

    [MaxLength(MaxRoles)]
    [ItemsMatch(ValidationPatterns.RoleName, 64)]
    public IReadOnlyList<string> Roles { get; init; } = [];

    [Required]
    public required UpdateSourceInfo UpdateSource { get; init; }

    /// <summary>Windows time zone ID of the server (e.g. "W. Europe Standard Time"); maintenance windows are in this time.</summary>
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.TimeZoneId)]
    public string? TimeZoneId { get; init; }

    /// <summary>Installed updates; null if they could not be read (the central server then keeps the previous list).</summary>
    [MaxLength(MaxInstalledUpdates)]
    public IReadOnlyList<InstalledUpdate>? InstalledUpdates { get; init; }
}

public sealed record OperatingSystemInfo
{
    /// <summary>Product name as shown by Windows, e.g. "Windows Server 2022 Standard".</summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public required string ProductName { get; init; }

    /// <summary>Edition identifier, e.g. "ServerStandard".</summary>
    [StringLength(64)]
    [RegularExpression("^[A-Za-z0-9]+$")]
    public string? EditionId { get; init; }

    /// <summary>Display version, e.g. "21H2"; not set on older releases.</summary>
    [StringLength(16)]
    [RegularExpression("^[A-Za-z0-9.]+$")]
    public string? DisplayVersion { get; init; }

    /// <summary>Full version including UBR, e.g. "10.0.20348.2700".</summary>
    [Required]
    [StringLength(23, MinimumLength = 3)]
    [RegularExpression(ValidationPatterns.OsVersion)]
    public required string Version { get; init; }
}

public sealed record UpdateSourceInfo
{
    /// <summary>True if a WSUS server is configured by policy (reported as a warning, WSUS is not desired).</summary>
    public bool UsesWsus { get; init; }

    [StringLength(255)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public string? WsusServer { get; init; }

    /// <summary>How Windows itself installs updates; null from agents before version 0.3.</summary>
    public AutomaticUpdatesInfo? AutomaticUpdates { get; init; }
}

/// <summary>
/// The Windows Automatic Updates mode, values as in the policy value AUOptions
/// (see docs/05-agent-design.md, section 3, "Windows Automatic Updates").
/// </summary>
public enum AutomaticUpdateMode
{
    /// <summary>Neither policy nor local setting: Windows Server 2016 and later does not install on its own.</summary>
    NotConfigured = 0,

    /// <summary>NoAutoUpdate = 1.</summary>
    Disabled = 1,

    NotifyDownload = 2,

    AutoDownloadNotifyInstall = 3,

    /// <summary>Windows downloads and installs on its own schedule, outside the coordination.</summary>
    ScheduledInstall = 4,

    LocalAdminChooses = 5,

    NotifyInstallAndRestart = 7,
}

public sealed record AutomaticUpdatesInfo
{
    [EnumDataType(typeof(AutomaticUpdateMode))]
    public AutomaticUpdateMode Mode { get; init; }

    /// <summary>True if set by group policy (or the policy registry key), false for the local setting.</summary>
    public bool ByPolicy { get; init; }

    /// <summary>Policy "Always automatically restart at the scheduled time".</summary>
    public bool ScheduledRestart { get; init; }

    /// <summary>A compliance deadline policy forces installation and restart after a deadline.</summary>
    public bool Deadline { get; init; }

    /// <summary>The policy value is the one the agent set on request of the central server (option 3), not group policy.</summary>
    public bool ManagedByAgent { get; init; }

    /// <summary>True if Windows installs or restarts on its own, bypassing rings and maintenance windows.</summary>
    public bool BypassesCoordination() => Mode == AutomaticUpdateMode.ScheduledInstall || ScheduledRestart || Deadline;
}

/// <summary>
/// An installed update, identified uniquely by update ID and revision (see docs/03-communication-protocol.md, section 6).
/// </summary>
public sealed record InstalledUpdate : UpdateInfo;
