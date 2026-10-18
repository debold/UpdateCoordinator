using System.Globalization;

namespace UpdateCoordinator.Server.Rollouts;

/// <summary>Geometry and text for the dashboard charts, shared by bar and donut.</summary>
internal static class Charts
{
    /// <summary>Segments in the order successful, pending, queued, failed, scaled to <paramref name="length"/>; empty states are left out.</summary>
    public static IEnumerable<(string Class, double Offset, double Length)> Segments(DeploymentCounts counts, double length)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Total == 0)
        {
            yield break;
        }

        var offset = 0.0;
        foreach (var (cls, value) in new[] { ("ok", counts.Successful), ("pend", counts.Pending), ("queue", counts.Queued), ("fail", counts.Failed) })
        {
            if (value == 0)
            {
                continue;
            }

            var segment = length * value / counts.Total;
            yield return (cls, offset, segment);
            offset += segment;
        }
    }

    public static string Describe(DeploymentCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return string.Create(CultureInfo.InvariantCulture, $"{counts.Successful} successful, {counts.Pending} pending, {counts.Queued} queued, {counts.Failed} failed");
    }

    /// <summary>Number for an SVG attribute: invariant culture, two decimals at most.</summary>
    public static string Number(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}
