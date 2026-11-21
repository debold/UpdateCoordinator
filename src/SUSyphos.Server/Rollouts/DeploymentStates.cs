namespace SUSyphos.Server.Rollouts;

/// <summary>
/// Names, colours and one-sentence explanations of the deployment states, the same everywhere: legends, bars, donuts,
/// pills and the help (Marc, 2026-11-20). "Waiting for release" has a calm grey-blue of its own (class "wait"); amber
/// ("queue") stays for things that need attention.
/// </summary>
internal static class DeploymentStates
{
    public const string Installed = "Installed";
    public const string ReadyToInstall = "Ready to install";
    public const string WaitingForRelease = "Waiting for release";
    public const string Failed = "Failed";
    public const string NotNeeded = "Not needed";

    /// <summary>CSS class of the waiting state (bars, donuts, pills, legend swatches).</summary>
    public const string WaitClass = "wait";

    /// <summary>The visible name of a state.</summary>
    public static string Label(DeploymentState state) => state switch
    {
        DeploymentState.Successful => Installed,
        DeploymentState.Pending => ReadyToInstall,
        DeploymentState.Queued => WaitingForRelease,
        _ => Failed,
    };

    /// <summary>The CSS class of a state ("ok", "pend", "wait", "fail").</summary>
    public static string Class(DeploymentState state) => state switch
    {
        DeploymentState.Successful => "ok",
        DeploymentState.Pending => "pend",
        DeploymentState.Queued => WaitClass,
        _ => "fail",
    };

    /// <summary>One sentence for the tooltip of a legend entry; the help uses the same sentences.</summary>
    public static string Explanation(DeploymentState state) => state switch
    {
        DeploymentState.Successful => "The server reported the update as installed.",
        DeploymentState.Pending => "Released for the server; it installs in its next maintenance window, or right now with Install now.",
        DeploymentState.Queued => "The server needs the update, but its ring has not released it yet.",
        _ => "The last installation on the server failed; the agent tries again in its next window.",
    };

    /// <summary>Tooltip of "Not needed" in the donuts.</summary>
    public const string NotNeededExplanation = "The server does not need the update (not applicable, or a newer one replaces it).";
}
