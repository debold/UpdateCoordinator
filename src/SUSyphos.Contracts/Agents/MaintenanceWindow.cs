using System.ComponentModel.DataAnnotations;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// A maintenance window on the given weekdays in local server time (docs/04-central-server.md, section 4).
/// If <see cref="End"/> is not after <see cref="Start"/>, the window runs over midnight into the next day.
/// </summary>
public sealed record MaintenanceWindow : IValidatableObject
{
    /// <summary>Upper bound for windows per server.</summary>
    public const int MaxWindows = 14;

    [MinLength(1)]
    [MaxLength(7)]
    public required IReadOnlyList<DayOfWeek> Days { get; init; }

    public required TimeSpan Start { get; init; }

    /// <summary>End of the window; up to 1.00:00:00 (end of day).</summary>
    public required TimeSpan End { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Days.Any(day => !Enum.IsDefined(day)) || Days.Distinct().Count() != Days.Count)
        {
            yield return new ValidationResult("Every weekday must be valid and listed once.", [nameof(Days)]);
        }

        if (Start < TimeSpan.Zero || Start >= TimeSpan.FromDays(1))
        {
            yield return new ValidationResult("A maintenance window must start between 00:00 and 23:59.", [nameof(Start)]);
        }

        if (End <= TimeSpan.Zero || End > TimeSpan.FromDays(1))
        {
            yield return new ValidationResult("A maintenance window must end after 00:00 and at the latest at 1.00:00:00.", [nameof(End)]);
        }
    }

    /// <summary>System default: Mon–Fri 19:00–05:00, Sat/Sun all day.</summary>
    public static IReadOnlyList<MaintenanceWindow> SystemDefault { get; } =
    [
        new MaintenanceWindow
        {
            Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            Start = TimeSpan.FromHours(19),
            End = TimeSpan.FromHours(5),
        },
        new MaintenanceWindow { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday], Start = TimeSpan.Zero, End = TimeSpan.FromDays(1) },
    ];
}
