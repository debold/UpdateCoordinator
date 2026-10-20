using Microsoft.EntityFrameworkCore.Query;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>
/// Live status of an agent (docs/05-agent-design.md, section 5): what it is doing right now and why a pending reboot waits.
/// One place for heartbeat and activity endpoint, so both store the same normalized values.
/// </summary>
internal static class LiveStatus
{
    /// <summary>
    /// An activity older than this is not shown: the agent sends it every few seconds while a step runs, so a stale entry
    /// means the agent stopped or crashed in the middle.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>Normalized activity as stored; the agent clock is not trusted, so an implausible start becomes "now".</summary>
    internal sealed record Values(AgentPhase? Phase, int? Percent, List<Guid> UpdateIds, DateTime? StartedAtUtc);

    public static Values Normalize(AgentActivity? activity, DateTime nowUtc)
    {
        if (activity is null || !Enum.IsDefined(activity.Phase))
        {
            return new Values(null, null, [], null);
        }

        var started = activity.StartedAt.UtcDateTime;
        if (started > nowUtc.AddHours(1) || started.Year < 2000)
        {
            started = nowUtc;
        }

        return new Values(
            activity.Phase,
            activity.Percent is { } percent ? Math.Clamp(percent, 0, 100) : null,
            [.. (activity.UpdateIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(AgentActivity.MaxUpdates)],
            started);
    }

    /// <summary>Adds the activity columns to a single parameterized UPDATE.</summary>
    public static void Apply(UpdateSettersBuilder<ClientEntity> setters, Values values, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(setters);
        ArgumentNullException.ThrowIfNull(values);
        setters
            .SetProperty(c => c.ActivityPhase, values.Phase)
            .SetProperty(c => c.ActivityPercent, values.Percent)
            .SetProperty(c => c.ActivityUpdateIds, values.UpdateIds)
            .SetProperty(c => c.ActivityStartedAtUtc, values.StartedAtUtc)
            .SetProperty(c => c.ActivityReportedAtUtc, nowUtc);
    }

    /// <summary>The reported activity if it is current, else null.</summary>
    public static AgentPhase? CurrentPhase(ClientEntity client, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.ActivityPhase is { } phase && client.ActivityReportedAtUtc is { } reported && nowUtc - reported <= StaleAfter ? phase : null;
    }

    public static string PhaseLabel(AgentPhase phase) => phase switch
    {
        AgentPhase.Searching => "Searching for updates",
        AgentPhase.Downloading => "Downloading",
        AgentPhase.Installing => "Installing",
        AgentPhase.Inventory => "Collecting inventory",
        _ => phase.ToString(),
    };

    /// <summary>One line for lists, e.g. "Downloading 45 %".</summary>
    public static string Describe(ClientEntity client, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(client);
        return CurrentPhase(client, nowUtc) is { } phase
            ? PhaseLabel(phase) + (client.ActivityPercent is { } percent ? $" {percent} %" : string.Empty)
            : "Idle";
    }

    /// <summary>Plain-language reason why a pending reboot waits.</summary>
    public static string RebootWaitText(RebootWait wait) => wait switch
    {
        RebootWait.Restarting => "the agent is restarting the server",
        RebootWait.NotAllowedByPolicy => "waits: the reboot policy is \"Never\"",
        RebootWait.LocalVeto => "waits: reboots are forbidden locally on this server (AllowReboot)",
        RebootWait.Paused => "waits: installations are paused (global pause or blackout)",
        RebootWait.OutsideWindow => "waits for the next maintenance window",
        RebootWait.WindowEndsSoon => "waits: the open window closes within 30 minutes",
        RebootWait.RecentlyRequested => "waits: a restart was requested within the last 6 hours and did not happen",
        RebootWait.WaitingForLease => "waits for a free slot in its coordinated group",
        _ => wait.ToString(),
    };
}
