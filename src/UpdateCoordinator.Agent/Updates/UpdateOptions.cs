using System.ComponentModel.DataAnnotations;

namespace UpdateCoordinator.Agent.Updates;

internal enum UpdateSourceSelection
{
    /// <summary>As configured on the server: Windows Update, or WSUS if set by group policy.</summary>
    System = 0,

    /// <summary>Always Windows Update directly, even if WSUS is configured.</summary>
    WindowsUpdate = 2,
}

/// <summary>
/// Update run settings. Bound from the "Updates" configuration section.
/// </summary>
internal sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>How often the pre-flight check and the search for available updates run.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "1.00:00:00")]
    public TimeSpan SearchInterval { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Free space required on the system drive; below it no search or installation runs.</summary>
    [Range(512, 1_000_000)]
    public int MinFreeDiskSpaceMegabytes { get; set; } = 4_096;

    public UpdateSourceSelection Source { get; set; } = UpdateSourceSelection.System;
}
