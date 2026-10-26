using System.Globalization;

namespace SUSyphos.Server.Rollouts;

/// <summary>Geometry and text for the dashboard charts, shared by bar and donut.</summary>
internal static class Charts
{
    /// <summary>Deployment states in the order successful, pending, queued, failed, not needed.</summary>
    public static IReadOnlyList<(string Class, int Value)> Parts(DeploymentCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return [("ok", counts.Successful), ("pend", counts.Pending), ("queue", counts.Queued), ("fail", counts.Failed), ("none", counts.NotNeeded)];
    }

    /// <summary>Segments in the order successful, pending, queued, failed, not needed, scaled to <paramref name="length"/>; empty states are left out.</summary>
    public static IEnumerable<(string Class, double Offset, double Length)> Segments(DeploymentCounts counts, double length) => Segments(Parts(counts), length);

    /// <summary>Segments of any split, in the given order, scaled to <paramref name="length"/>; empty parts are left out.</summary>
    public static IEnumerable<(string Class, double Offset, double Length)> Segments(IReadOnlyList<(string Class, int Value)> parts, double length)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var total = parts.Sum(p => p.Value);
        if (total == 0)
        {
            yield break;
        }

        var offset = 0.0;
        foreach (var (cls, value) in parts)
        {
            if (value == 0)
            {
                continue;
            }

            var segment = length * value / total;
            yield return (cls, offset, segment);
            offset += segment;
        }
    }

    public static string Describe(DeploymentCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return string.Create(CultureInfo.InvariantCulture, $"{counts.Successful} successful, {counts.Pending} pending, {counts.Queued} queued, {counts.Failed} failed{(counts.NotNeeded > 0 ? $", {counts.NotNeeded} not needed" : string.Empty)}");
    }

    /// <summary>Text for screen readers of any split, e.g. "3 ok, 1 fail".</summary>
    public static string Describe(IReadOnlyList<(string Class, int Value)> parts) =>
        string.Join(", ", (parts ?? []).Where(p => p.Value > 0).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Value} {p.Class}")));

    /// <summary>Lines a donut legend shows at most, so tiles in a row keep their height (docs/06-dashboard.md, section 8 "Charts").</summary>
    public const int LegendLines = 4;

    /// <summary>Deployment states with their labels, for a donut with legend.</summary>
    public static IReadOnlyList<DonutPart> Labeled(DeploymentCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return [new("ok", "Successful", counts.Successful), new("pend", "Pending", counts.Pending), new("queue", "Queued", counts.Queued), new("fail", "Failed", counts.Failed), new("none", "Not needed", counts.NotNeeded)];
    }

    /// <summary>The parts of a donut as donut and legend show them: largest first, empty parts left out (equal counts keep their order).</summary>
    public static IReadOnlyList<DonutPart> Sorted(IEnumerable<DonutPart> parts) =>
        (parts ?? []).Where(p => p.Value > 0).OrderByDescending(p => p.Value).ToList();

    /// <summary>The legend lines: up to <paramref name="lines"/> parts, or one line fewer and the rest summed up as "+N more".</summary>
    public static (IReadOnlyList<DonutPart> Shown, IReadOnlyList<DonutPart> More) Legend(IReadOnlyList<DonutPart> sorted, int lines = LegendLines)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        return sorted.Count <= lines ? (sorted, []) : (sorted.Take(lines - 1).ToList(), sorted.Skip(lines - 1).ToList());
    }

    /// <summary>Number for an SVG attribute: invariant culture, two decimals at most.</summary>
    public static string Number(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Size of a donut: 40 px in table cells, 64 px in tiles, 120 px in boxes (docs/06-dashboard.md, section 8 "Charts").</summary>
public enum DonutSize
{
    Cell,
    Tile,
    Box,
}

/// <summary>One part of a donut with legend: status class, label, count and an optional link (e.g. the filtered list).</summary>
public sealed record DonutPart(string Class, string Label, int Value, string? Href = null);
