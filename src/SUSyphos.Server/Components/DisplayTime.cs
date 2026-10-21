using System.Globalization;

namespace SUSyphos.Server.Components;

/// <summary>
/// Times in the dashboard: stored and exchanged with agents in UTC, shown and entered in the time zone of the central
/// server (its Windows time zone). One place, so every page shows the same zone in the same 24-hour format.
/// </summary>
public static class DisplayTime
{
    /// <summary>Time zone of the central server.</summary>
    public static TimeZoneInfo Zone => TimeZoneInfo.Local;

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>Converts a time entered in the zone of the central server to UTC; times skipped by a clock change move forward.</summary>
    public static DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, Zone);
    }

    /// <summary>"2026-10-20 16:30" in the zone of the central server; "–" for no value.</summary>
    public static string Format(DateTime? utc) =>
        utc is { } value ? ToLocal(value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "–";

    /// <summary>With seconds, for the audit log.</summary>
    public static string FormatSeconds(DateTime utc) => ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string FormatDate(DateTime utc) => ToLocal(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
