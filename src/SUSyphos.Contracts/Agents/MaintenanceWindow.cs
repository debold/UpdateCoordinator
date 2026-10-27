using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Occurrence of a weekday within its month: the first to fourth, or the last one (which can also be the fourth).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WeekOfMonth>))]
public enum WeekOfMonth
{
    First = 1,
    Second = 2,
    Third = 3,
    Fourth = 4,
    Last = 5,
}

/// <summary>
/// A maintenance window for one span of time (Marc, 2026-10-27), in addition to the weekly windows: start and end in UTC,
/// at most <see cref="MaxLength"/> long. The agent turns it into one <see cref="MaintenanceWindow"/> per local day.
/// </summary>
public sealed record OneTimeWindow(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Upper bound for one-time windows in a release list.</summary>
    public const int MaxWindows = 30;

    /// <summary>Longest one-time window.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(7);

    public bool IsValid => End > Start && End - Start <= MaxLength;

    /// <summary>The window as weekly-window entries for the local days it covers (local time of <paramref name="zone"/>).</summary>
    public IEnumerable<MaintenanceWindow> ToLocalDays(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var start = TimeZoneInfo.ConvertTime(Start, zone).DateTime;
        var end = TimeZoneInfo.ConvertTime(End, zone).DateTime;
        for (var day = start.Date; day < end; day = day.AddDays(1))
        {
            var from = day == start.Date ? start.TimeOfDay : TimeSpan.Zero;
            var to = day == end.Date ? end.TimeOfDay : TimeSpan.FromDays(1);
            if (to > from)
            {
                yield return new MaintenanceWindow { Days = [day.DayOfWeek], Start = from, End = to, On = DateOnly.FromDateTime(day) };
            }
        }
    }
}

/// <summary>
/// A maintenance window on the given weekdays in local server time (docs/04-central-server.md, section 4).
/// If <see cref="End"/> is not after <see cref="Start"/>, the window runs over midnight into the next day.
/// <see cref="Weeks"/> limits it to some occurrences of the weekday in the month (e.g. the second Tuesday),
/// <see cref="ExceptWeeks"/> leaves some out (e.g. every Saturday except the last); the day the window starts counts.
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

    /// <summary>Only these occurrences of the weekday in the month; null or empty means every week.</summary>
    [MaxLength(5)]
    public IReadOnlyList<WeekOfMonth>? Weeks { get; init; }

    /// <summary>Not on these occurrences of the weekday in the month; null or empty leaves none out.</summary>
    [MaxLength(5)]
    public IReadOnlyList<WeekOfMonth>? ExceptWeeks { get; init; }

    /// <summary>True if the window has a rule on weeks of the month (agents before 0.7.1 do not understand it).</summary>
    [JsonIgnore]
    public bool HasWeekRule => Weeks is { Count: > 0 } || ExceptWeeks is { Count: > 0 };

    /// <summary>
    /// Only on this local date: one day of a one-time window (<see cref="OneTimeWindow"/>), built by the agent from the
    /// release list and never sent over the wire, so every calculation on windows treats it like a weekly window.
    /// </summary>
    [JsonIgnore]
    public DateOnly? On { get; init; }

    /// <summary>True if a window of this kind starts on <paramref name="date"/>: the weekday and the week rule match, or the date of a one-time window.</summary>
    public bool StartsOn(DateTime date)
    {
        if (On is { } on)
        {
            return DateOnly.FromDateTime(date) == on;
        }

        if (!Days.Contains(date.DayOfWeek))
        {
            return false;
        }

        var occurrence = ((date.Day - 1) / 7) + 1;
        var last = date.Day + 7 > DateTime.DaysInMonth(date.Year, date.Month);
        bool Matches(WeekOfMonth week) => week == WeekOfMonth.Last ? last : (int)week == occurrence;
        return (Weeks is not { Count: > 0 } weeks || weeks.Any(Matches))
            && (ExceptWeeks is not { Count: > 0 } except || !except.Any(Matches));
    }

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

        foreach (var (list, name) in new[] { (Weeks, nameof(Weeks)), (ExceptWeeks, nameof(ExceptWeeks)) })
        {
            if (list is not null && (list.Any(week => !Enum.IsDefined(week)) || list.Distinct().Count() != list.Count))
            {
                yield return new ValidationResult("Every week of the month must be valid and listed once.", [name]);
            }
        }

        if (Weeks is { Count: > 0 } && ExceptWeeks is { Count: > 0 })
        {
            yield return new ValidationResult("A window either names its weeks or the weeks it leaves out, not both.", [nameof(ExceptWeeks)]);
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
