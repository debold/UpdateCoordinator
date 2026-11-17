using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Components;

/// <summary>
/// The update list filtered to one ring and a deployment state there (Marc, 2026-11-17, mockup
/// https://claude.ai/artifact/1UnpVC3i3ZcN1c7gpY1NpJ): the parts of the ring bar of the overview link to it, and the
/// update list reads it back. The state names the colour of the bar: successful, pending, queued or failed.
/// </summary>
internal static class RingLink
{
    /// <summary>Query parameter with the name of the ring.</summary>
    public const string RingParameter = "ring";

    /// <summary>Query parameter with the deployment state in that ring.</summary>
    public const string StateParameter = "ring-state";

    private static readonly (string Key, string Class, DeploymentState State, string Label)[] States =
    [
        ("successful", "ok", DeploymentState.Successful, "Successful"),
        ("pending", "pend", DeploymentState.Pending, "Pending"),
        ("queued", "queue", DeploymentState.Queued, "Queued"),
        ("failed", "fail", DeploymentState.Failed, "Failed"),
    ];

    /// <summary>The update list for a ring, optionally only with updates in one state there.</summary>
    public static string Link(string ringName, DeploymentState? state = null)
    {
        ArgumentNullException.ThrowIfNull(ringName);
        var link = $"/updates?{RingParameter}={Uri.EscapeDataString(ringName)}";
        return state is { } chosen ? $"{link}&{StateParameter}={KeyOf(chosen)}" : link;
    }

    /// <summary>The state behind a segment class of the bar ("ok", "pend", "queue", "fail"); null for others.</summary>
    public static DeploymentState? StateOfClass(string? segmentClass) =>
        States.FirstOrDefault(s => s.Class == segmentClass) is { Key: not null } match ? match.State : null;

    /// <summary>The state a query value names; null when it is missing or unknown.</summary>
    public static DeploymentState? Parse(string? value) =>
        States.FirstOrDefault(s => string.Equals(s.Key, value, StringComparison.OrdinalIgnoreCase)) is { Key: not null } match ? match.State : null;

    public static string Label(DeploymentState state) => States.First(s => s.State == state).Label;

    /// <summary>How many server × update pairs of the counts are in the state.</summary>
    public static int Count(DeploymentCounts counts, DeploymentState state)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return state switch
        {
            DeploymentState.Successful => counts.Successful,
            DeploymentState.Pending => counts.Pending,
            DeploymentState.Queued => counts.Queued,
            _ => counts.Failed,
        };
    }

    private static string KeyOf(DeploymentState state) => States.First(s => s.State == state).Key;
}
