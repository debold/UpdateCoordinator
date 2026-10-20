using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// What the agent is doing right now (live status, docs/05-agent-design.md, section 5). Sent with every heartbeat and,
/// while a long step runs, on its own every few seconds, because the heartbeat loop waits for that step.
/// </summary>
public sealed record AgentActivity
{
    /// <summary>Upper bound for the update IDs of one activity (an installation batch is never larger).</summary>
    public const int MaxUpdates = 100;

    [Required]
    public required AgentPhase Phase { get; init; }

    /// <summary>Progress in percent where Windows reports it (download and installation); null otherwise.</summary>
    [Range(0, 100)]
    public int? Percent { get; init; }

    /// <summary>Updates of the current download or installation.</summary>
    [MaxLength(MaxUpdates)]
    public IReadOnlyList<Guid> UpdateIds { get; init; } = [];

    [Required]
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>
/// Step the agent is working on.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentPhase>))]
public enum AgentPhase
{
    /// <summary>Searching for available updates.</summary>
    Searching = 0,

    /// <summary>Downloading released updates.</summary>
    Downloading = 1,

    /// <summary>Installing released updates.</summary>
    Installing = 2,

    /// <summary>Collecting the inventory.</summary>
    Inventory = 3,
}

/// <summary>
/// Live status sent between heartbeats while a long step runs; null <see cref="Activity"/> means the step finished.
/// </summary>
public sealed record AgentActivityReport
{
    public AgentActivity? Activity { get; init; }
}

/// <summary>
/// Why a pending reboot has not happened (yet), or that the agent is restarting the server (docs/05-agent-design.md, section 4).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RebootWait>))]
public enum RebootWait
{
    /// <summary>The agent requested the restart; it follows after the warning period.</summary>
    Restarting = 0,

    /// <summary>The effective reboot policy is "Never".</summary>
    NotAllowedByPolicy = 1,

    /// <summary>The local veto (registry value AllowReboot or Enabled) forbids it.</summary>
    LocalVeto = 2,

    /// <summary>Installations are paused globally or by a blackout period.</summary>
    Paused = 3,

    /// <summary>No maintenance window is open.</summary>
    OutsideWindow = 4,

    /// <summary>The open window closes within 30 minutes.</summary>
    WindowEndsSoon = 5,

    /// <summary>A restart was requested within the last 6 hours and did not happen.</summary>
    RecentlyRequested = 6,

    /// <summary>All leases of the coordinated group are taken.</summary>
    WaitingForLease = 7,
}
