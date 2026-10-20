namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Time calculations on maintenance windows in local server time, shared by the agent (when to install) and the
/// central server (when the next window opens).
/// </summary>
public static class MaintenanceSchedule
{
    /// <summary>True if <paramref name="localTime"/> lies in one of the windows.</summary>
    public static bool IsOpen(IReadOnlyList<MaintenanceWindow> windows, DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var time = localTime.TimeOfDay;
        var today = localTime.Date;
        var yesterday = today.AddDays(-1);

        // A window that runs over midnight belongs to the day it starts; its week rule is checked for that day.
        return windows.Any(window => window.End > window.Start
            ? window.StartsOn(today) && time >= window.Start && time < window.End
            : (window.StartsOn(today) && time >= window.Start) || (window.StartsOn(yesterday) && time < window.End));
    }

    /// <summary>
    /// Start of the next window after <paramref name="localTime"/>, or <paramref name="localTime"/> itself if a window is open;
    /// null if there are no windows.
    /// </summary>
    public static DateTime? NextOpening(IReadOnlyList<MaintenanceWindow> windows, DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (IsOpen(windows, localTime))
        {
            return localTime;
        }

        // Weekly windows open within a week; windows on some weeks of the month within about two months.
        DateTime? next = null;
        var horizon = windows.Any(w => w.HasWeekRule) ? 70 : 7;
        for (var offset = 0; offset <= horizon && next is null; offset++)
        {
            var day = localTime.Date.AddDays(offset);
            foreach (var window in windows.Where(w => w.StartsOn(day)))
            {
                var start = day + window.Start;
                if (start > localTime && (next is null || start < next))
                {
                    next = start;
                }
            }
        }

        return next;
    }

    /// <summary>
    /// The next window as local start and end (the start is <paramref name="localTime"/> while a window is open); overlapping
    /// and adjoining windows count as one. Null if there are no windows. Windows end on whole minutes.
    /// </summary>
    public static (DateTime Start, DateTime End)? NextWindow(IReadOnlyList<MaintenanceWindow> windows, DateTime localTime)
    {
        if (NextOpening(windows, localTime) is not { } start)
        {
            return null;
        }

        // Walk forward minute by minute; a week bounds it for a window that never closes (e.g. every day all day).
        var end = start;
        var limit = start.AddDays(7);
        while (end < limit && IsOpen(windows, end))
        {
            end = end.AddMinutes(1);
        }

        return (start, end);
    }
}
