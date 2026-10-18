using System.ComponentModel.DataAnnotations;

namespace UpdateCoordinator.Agent.Installation;

/// <summary>
/// A maintenance window on the given weekdays in local server time. If <see cref="End"/> is not after
/// <see cref="Start"/>, the window runs over midnight into the next day.
/// </summary>
internal sealed class MaintenanceWindowRule
{
    public List<DayOfWeek> Days { get; set; } = [];

    public TimeSpan Start { get; set; }

    /// <summary>End of the window; up to 1.00:00:00 (end of day).</summary>
    public TimeSpan End { get; set; }
}

/// <summary>
/// Installation settings. Bound from the "Installation" configuration section.
/// Group and server specific windows follow with the ring logic.
/// </summary>
internal sealed class InstallationOptions : IValidatableObject
{
    public const string SectionName = "Installation";

    /// <summary>System default (docs/04-central-server.md, section 4): Mon–Fri 19:00–05:00, Sat/Sun all day.</summary>
    public static IReadOnlyList<MaintenanceWindowRule> DefaultWindows { get; } =
    [
        new MaintenanceWindowRule
        {
            Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            Start = TimeSpan.FromHours(19),
            End = TimeSpan.FromHours(5),
        },
        new MaintenanceWindowRule
        {
            Days = [DayOfWeek.Saturday, DayOfWeek.Sunday],
            Start = TimeSpan.Zero,
            End = TimeSpan.FromDays(1),
        },
    ];

    /// <summary>False disables installations completely (search and reporting continue).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maintenance windows; empty means the system default.</summary>
    public List<MaintenanceWindowRule> Windows { get; set; } = [];

    /// <summary>Releases due within this time after a due batch are installed together with it, to avoid extra reboots.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "04:00:00")]
    public TimeSpan PullForwardTolerance { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>An update that was already attempted is not tried again before this time has passed.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromHours(6);

    public IReadOnlyList<MaintenanceWindowRule> EffectiveWindows => Windows.Count > 0 ? Windows : DefaultWindows;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var window in Windows)
        {
            if (window.Days.Count == 0 || window.Days.Any(day => !Enum.IsDefined(day)))
            {
                yield return new ValidationResult("Every maintenance window needs at least one valid weekday.", [nameof(Windows)]);
            }

            if (window.Start < TimeSpan.Zero || window.Start >= TimeSpan.FromDays(1))
            {
                yield return new ValidationResult("A maintenance window must start between 00:00 and 23:59.", [nameof(Windows)]);
            }

            if (window.End <= TimeSpan.Zero || window.End > TimeSpan.FromDays(1))
            {
                yield return new ValidationResult("A maintenance window must end after 00:00 and at the latest at 1.00:00:00.", [nameof(Windows)]);
            }
        }
    }
}
