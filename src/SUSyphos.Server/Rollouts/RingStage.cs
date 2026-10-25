using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>
/// The stage of an update in one ring as word and status colour, the same in the update list and on the update page
/// (docs/06-dashboard.md, section 8 "Status indicators").
/// </summary>
internal static class RingStage
{
    /// <param name="state">State of the ring in the rollout; null when the rollout has not reached it or there is none.</param>
    /// <param name="ring">Installation states of the servers of the ring; when given, a released ring says whether it is done.</param>
    public static (string Label, string Class) Of(RingRolloutState? state, DeploymentCounts? ring = null) => state switch
    {
        RingRolloutState.Released when ring is { Failed: > 0 } => ("Failures", "fail"),
        RingRolloutState.Released when ring is { Pending: > 0 } => ("Rolling out", "pend"),
        RingRolloutState.Released => ("Released", "ok"),
        RingRolloutState.AwaitingApproval => ("Awaiting approval", "queue"),
        RingRolloutState.Alarm => ("Alarm", "fail"),
        RingRolloutState.Waiting => ("Waiting", "neutral"),
        _ => ("Not started", "neutral"),
    };
}
