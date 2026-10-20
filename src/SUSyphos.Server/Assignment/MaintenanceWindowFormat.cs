using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Server.Assignment;

/// <summary>
/// Text format for maintenance windows in the dashboard, one window per line:
/// <c>Mon-Fri 19:00-05:00</c>, <c>Sat,Sun 00:00-24:00</c>. Also the JSON form stored in the database.
/// </summary>
internal static class MaintenanceWindowFormat
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses the text form. Returns false with an error message for any invalid line.</summary>
    public static bool TryParse(string? text, out List<MaintenanceWindow> windows, out string? error)
    {
        windows = [];
        error = null;
        var lines = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaintenanceWindow.MaxWindows)
        {
            error = $"At most {MaintenanceWindow.MaxWindows} windows are allowed.";
            return false;
        }

        foreach (var line in lines)
        {
            if (!TryParseLine(line, out var window))
            {
                error = $"Invalid window \"{(line.Length > 60 ? line[..60] : line)}\". Expected for example \"Mon-Fri 19:00-05:00\".";
                return false;
            }

            windows.Add(window);
        }

        return true;
    }

    public static string Format(IEnumerable<MaintenanceWindow> windows)
    {
        var builder = new StringBuilder();
        foreach (var window in windows)
        {
            builder.Append(string.Join(',', window.Days.Select(d => DayNames[(int)d])))
                .Append(' ')
                .Append(FormatTime(window.Start))
                .Append('-')
                .Append(FormatTime(window.End))
                .Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    public static string ToJson(IReadOnlyList<MaintenanceWindow> windows) => JsonSerializer.Serialize(windows, Json);

    /// <summary>Reads stored JSON; null or invalid content yields null so the next level applies.</summary>
    public static IReadOnlyList<MaintenanceWindow>? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var windows = JsonSerializer.Deserialize<List<MaintenanceWindow>>(json, Json);
            return windows is { Count: > 0 } && windows.All(IsValid) ? windows : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseLine(string line, out MaintenanceWindow window)
    {
        window = null!;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TryParseDays(parts[0], out var days) || !TryParseRange(parts[1], out var start, out var end))
        {
            return false;
        }

        window = new MaintenanceWindow { Days = days, Start = start, End = end };
        return IsValid(window);
    }

    private static bool TryParseDays(string text, out List<DayOfWeek> days)
    {
        days = [];
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = token.Split('-');
            if (range.Length == 1 && TryDay(range[0], out var day))
            {
                days.Add(day);
            }
            else if (range.Length == 2 && TryDay(range[0], out var from) && TryDay(range[1], out var to))
            {
                // Ranges follow the week from Monday to Sunday, e.g. Mon-Fri or Fri-Sun.
                for (var d = (int)from; ; d = (d + 1) % 7)
                {
                    days.Add((DayOfWeek)d);
                    if (d == (int)to)
                    {
                        break;
                    }
                }
            }
            else
            {
                return false;
            }
        }

        days = [.. days.Distinct()];
        return days.Count > 0;
    }

    private static bool TryDay(string text, out DayOfWeek day)
    {
        var index = Array.FindIndex(DayNames, name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
        day = (DayOfWeek)Math.Max(index, 0);
        return index >= 0;
    }

    private static bool TryParseRange(string text, out TimeSpan start, out TimeSpan end)
    {
        start = end = TimeSpan.Zero;
        var parts = text.Split('-');
        return parts.Length == 2 && TryTime(parts[0], out start) && TryTime(parts[1], out end);
    }

    private static bool TryTime(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (text == "24:00")
        {
            time = TimeSpan.FromDays(1);
            return true;
        }

        return text.Length == 5
            && TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time);
    }

    private static string FormatTime(TimeSpan time) =>
        time >= TimeSpan.FromDays(1) ? "24:00" : time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static bool IsValid(MaintenanceWindow window) =>
        window.Days is not null && Validator.TryValidateObject(window, new ValidationContext(window), null, validateAllProperties: true);
}
