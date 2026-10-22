namespace SUSyphos.Server.Administration;

/// <summary>State of the agent on a server, as the small mark next to its version shows it.</summary>
internal enum AgentState
{
    Current,
    UpdateAvailable,
    Failed,

    /// <summary>Version unknown, or below the minimum without a package to update to: needs a look.</summary>
    Other,
}

/// <summary>How recent the last heartbeat of a server is.</summary>
internal enum Freshness
{
    Current,
    Older,
    Stale,
}

/// <summary>The small marks of the server list (agent version, last heartbeat); one place for labels and classes.</summary>
internal static class ServerIndicators
{
    /// <summary>A heartbeat up to this age is current (agents send one every few seconds to minutes).</summary>
    public static readonly TimeSpan CurrentFor = TimeSpan.FromMinutes(5);

    public static AgentState Agent(string? agentUpdateError, bool versionKnown, string? targetVersion) =>
        agentUpdateError is not null ? AgentState.Failed
        : !versionKnown ? AgentState.Other
        : targetVersion is not null ? AgentState.UpdateAvailable
        : AgentState.Current;

    public static Freshness Heartbeat(DateTime? lastHeartbeatUtc, DateTime nowUtc) =>
        lastHeartbeatUtc is not { } heartbeat ? Freshness.Stale
        : nowUtc - heartbeat <= CurrentFor ? Freshness.Current
        : nowUtc - heartbeat <= ServerHealth.OfflineAfter ? Freshness.Older
        : Freshness.Stale;

    public static string Label(AgentState state) => state switch
    {
        AgentState.Current => "Current",
        AgentState.UpdateAvailable => "Update available",
        AgentState.Failed => "Update failed",
        _ => "Other",
    };

    /// <summary>Mark shown next to the version: check, arrow up, dash or dots.</summary>
    public static string Symbol(AgentState state) => state switch
    {
        AgentState.Current => "✓",
        AgentState.UpdateAvailable => "↑",
        AgentState.Failed => "–",
        _ => "…",
    };

    public static string Class(AgentState state) => state switch
    {
        AgentState.Current => "ok",
        AgentState.UpdateAvailable => "pend",
        AgentState.Failed => "fail",
        _ => "neutral",
    };

    public static string Label(Freshness freshness) => freshness switch
    {
        Freshness.Current => "Current",
        Freshness.Older => "Older",
        _ => "Long ago",
    };

    public static string Class(Freshness freshness) => freshness switch
    {
        Freshness.Current => "ok",
        Freshness.Older => "queue",
        _ => "fail",
    };
}
