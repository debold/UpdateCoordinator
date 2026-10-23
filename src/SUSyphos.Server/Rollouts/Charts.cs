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

    /// <summary>Number for an SVG attribute: invariant culture, two decimals at most.</summary>
    public static string Number(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}
