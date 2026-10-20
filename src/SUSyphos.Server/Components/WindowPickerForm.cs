using System.Globalization;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Assignment;

namespace SUSyphos.Server.Components;

/// <summary>
/// Field names, choices and the read-back of the maintenance window picker. The posted rows are turned into the text
/// form of <see cref="MaintenanceWindowFormat"/>, so validation stays in one place.
/// </summary>
internal static class WindowPickerForm
{
    public static readonly DayOfWeek[] WeekOrder = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Rows the picker renders at most: the allowed windows plus the empty rows.</summary>
    public const int MaxRows = MaintenanceWindow.MaxWindows + 2;

    private static readonly TimeSpan Step = TimeSpan.FromMinutes(30);

    public static string InheritField(string prefix) => $"{prefix}.inherit";

    public static string DaysField(string prefix, int row) => $"{prefix}.{row.ToString(CultureInfo.InvariantCulture)}.days";

    public static string FromField(string prefix, int row) => $"{prefix}.{row.ToString(CultureInfo.InvariantCulture)}.from";

    public static string ToField(string prefix, int row) => $"{prefix}.{row.ToString(CultureInfo.InvariantCulture)}.to";

    public static string ShortName(DayOfWeek day) => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(int)day][..2];

    public static string Format(TimeSpan time) => time >= TimeSpan.FromDays(1) ? "24:00" : time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>Half-hour steps; an existing value off the grid is kept selectable.</summary>
    public static IEnumerable<TimeSpan> Times(TimeSpan? current, bool includeEndOfDay)
    {
        var times = new SortedSet<TimeSpan>();
        for (var time = includeEndOfDay ? Step : TimeSpan.Zero; includeEndOfDay ? time <= TimeSpan.FromDays(1) : time < TimeSpan.FromDays(1); time += Step)
        {
            times.Add(time);
        }

        if (current is { } value)
        {
            times.Add(value);
        }

        return times;
    }

    /// <summary>
    /// Reads the posted picker. Returns the text form for <see cref="MaintenanceWindowFormat.TryParse"/>: empty when the
    /// level inherits or no row has days. Values are only compared with known choices.
    /// </summary>
    public static string Read(IFormCollection form, string prefix, out bool inherit)
    {
        ArgumentNullException.ThrowIfNull(form);
        inherit = form[InheritField(prefix)] == "yes";
        if (inherit)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        for (var row = 0; row < MaxRows; row++)
        {
            var days = form[DaysField(prefix, row)]
                .Select(v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 0 and <= 6 ? (DayOfWeek?)day : null)
                .Where(d => d is not null)
                .Select(d => d!.Value)
                .Distinct()
                .ToList();
            if (days.Count == 0)
            {
                continue;
            }

            // Unknown times make the line invalid, so the parser reports it instead of silently using a default.
            var from = form[FromField(prefix, row)].ToString();
            var to = form[ToField(prefix, row)].ToString();
            lines.Add($"{string.Join(',', days.OrderBy(d => Array.IndexOf(WeekOrder, d)).Select(d => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(int)d]))} {from}-{to}");
        }

        return string.Join('\n', lines);
    }

    public static string ActionField(string prefix) => $"{prefix}.action";

    /// <summary>
    /// The picker's own buttons (add, remove, refresh preview) post the form without saving. Returns the rows as entered,
    /// with the action applied, or null if the form was submitted to save.
    /// </summary>
    public static WindowPickerState? ReadEdit(IFormCollection form, string prefix)
    {
        ArgumentNullException.ThrowIfNull(form);
        var action = form[ActionField(prefix)].ToString();
        if (string.IsNullOrEmpty(action))
        {
            return null;
        }

        var drafts = new List<WindowDraft>();
        for (var row = 0; row < MaxRows; row++)
        {
            if (!form.ContainsKey(FromField(prefix, row)))
            {
                continue;
            }

            var days = form[DaysField(prefix, row)]
                .Select(v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 0 and <= 6 ? (DayOfWeek?)day : null)
                .Where(d => d is not null)
                .Select(d => d!.Value)
                .Distinct()
                .ToList();
            drafts.Add(new WindowDraft(days, ParseTime(form[FromField(prefix, row)]) ?? DefaultStart, ParseTime(form[ToField(prefix, row)]) ?? DefaultEnd));
        }

        if (action == "add" && drafts.Count < MaintenanceWindow.MaxWindows)
        {
            drafts.Add(new WindowDraft([], DefaultStart, DefaultEnd));
        }
        else if (action.StartsWith("remove:", StringComparison.Ordinal)
            && int.TryParse(action["remove:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index < drafts.Count)
        {
            drafts.RemoveAt(index);
        }

        return new WindowPickerState(drafts, form[InheritField(prefix)] == "yes");
    }

    /// <summary>The drafts that form a valid window (days chosen, start and end differ), for the preview.</summary>
    public static IReadOnlyList<MaintenanceWindow> ToWindows(IEnumerable<WindowDraft> drafts) =>
        [.. drafts.Where(d => d.Days.Count > 0 && d.Start != d.End).Select(d => new MaintenanceWindow { Days = d.Days, Start = d.Start, End = d.End })];

    public static readonly TimeSpan DefaultStart = TimeSpan.FromHours(19);

    public static readonly TimeSpan DefaultEnd = TimeSpan.FromHours(5);

    private static TimeSpan? ParseTime(string? value) =>
        value == "24:00" ? TimeSpan.FromDays(1)
        : TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var time) && time.Ticks % Step.Ticks == 0 ? time : null;

    /// <summary>Hours of the week (Sunday = 0) in which an installation may start.</summary>
    public static bool[,] Coverage(IReadOnlyList<MaintenanceWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var cover = new bool[7, 24];
        foreach (var window in windows)
        {
            foreach (var day in window.Days)
            {
                var start = (int)day * 24 * 60 + (int)window.Start.TotalMinutes;
                var end = (int)day * 24 * 60 + (int)window.End.TotalMinutes;
                if (window.End <= window.Start)
                {
                    end += 24 * 60;
                }

                for (var minute = start; minute < end; minute += 30)
                {
                    var hour = (minute / 60) % (7 * 24);
                    cover[hour / 24, hour % 24] = true;
                }
            }
        }

        return cover;
    }

    /// <summary>E.g. "Mon–Fri 19:00–05:00; Sat, Sun 00:00–24:00".</summary>
    public static string Describe(IReadOnlyList<MaintenanceWindow> windows) =>
        string.Join("; ", windows.Select(w => $"{Days(w.Days)} {Format(w.Start)}–{Format(w.End)}"));

    private static string Days(IReadOnlyList<DayOfWeek> days)
    {
        var ordered = days.OrderBy(d => Array.IndexOf(WeekOrder, d)).ToList();
        var indexes = ordered.Select(d => Array.IndexOf(WeekOrder, d)).ToList();
        var name = (DayOfWeek d) => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(int)d];
        return indexes.Count >= 3 && indexes[^1] - indexes[0] == indexes.Count - 1
            ? $"{name(ordered[0])}–{name(ordered[^1])}"
            : string.Join(", ", ordered.Select(name));
    }
}

/// <summary>A window row as entered, possibly without days yet.</summary>
public sealed record WindowDraft(IReadOnlyList<DayOfWeek> Days, TimeSpan Start, TimeSpan End);

/// <summary>The picker rows after one of its own buttons, shown again without saving.</summary>
public sealed record WindowPickerState(IReadOnlyList<WindowDraft> Drafts, bool Inherit);
