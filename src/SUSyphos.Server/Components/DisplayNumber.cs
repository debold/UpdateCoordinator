using System.Globalization;

namespace SUSyphos.Server.Components;

/// <summary>
/// Numbers in the dashboard (docs/06-dashboard.md, section 8 "Texts and formats"): digit grouping and decimal separator
/// come from the regional settings of the central server's operating system, never hard-coded (Marc, 2026-10-25); a
/// percentage is the number, a space and "%".
/// </summary>
public static class DisplayNumber
{
    /// <summary>The regional settings of the central server (the culture its process runs with).</summary>
    public static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Format(int value) => value.ToString("N0", Culture);

    public static string Format(long value) => value.ToString("N0", Culture);

    /// <summary>"75 %"; "–" for no value.</summary>
    public static string Percent(int? value) => value is { } percent ? $"{Format(percent)} %" : "–";

    /// <summary>Share of <paramref name="part"/> in <paramref name="total"/> as whole percent, rounded down; null for an empty total.</summary>
    public static int? Share(int part, int total) => total <= 0 ? null : (int)(100L * part / total);

    /// <summary>"v0.7.7" for a version; "–" for none.</summary>
    public static string Version(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "–" : version.StartsWith('v') ? version : "v" + version;
}
