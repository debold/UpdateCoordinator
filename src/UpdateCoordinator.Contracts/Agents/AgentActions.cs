using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Remote actions an administrator queues for an agent (docs/03-communication-protocol.md, action queue). The central
/// server never calls the agent: the heartbeat announces pending actions and the agent fetches them.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentActionKind>))]
public enum AgentActionKind
{
    /// <summary>Search for updates now and report the result.</summary>
    SearchNow = 0,

    /// <summary>Send the inventory and search for updates now.</summary>
    FullResync = 1,

    /// <summary>Restart the agent service.</summary>
    RestartService = 2,

    /// <summary>Restart the server now, outside the maintenance window if needed; the local veto still applies.</summary>
    RestartServer = 3,

    /// <summary>Upload the agent log files of the last days.</summary>
    UploadLogs = 4,
}

/// <summary>One queued action.</summary>
public sealed record AgentAction(Guid Id, AgentActionKind Kind, DateTimeOffset RequestedAt);

/// <summary>Actions waiting for the agent, oldest first.</summary>
public sealed record AgentActionList(IReadOnlyList<AgentAction> Actions)
{
    /// <summary>Upper bound per fetch; further actions follow with the next heartbeat.</summary>
    public const int MaxActions = 20;

    /// <summary>Upper bound of an uploaded log archive.</summary>
    public const int MaxLogBytes = 10 * 1024 * 1024;

    /// <summary>Log files of this many days are uploaded.</summary>
    public const int LogDays = 3;
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentActionStatus>))]
public enum AgentActionStatus
{
    Succeeded = 0,
    Failed = 1,

    /// <summary>The agent refused the action, e.g. because of a local setting.</summary>
    Rejected = 2,
}

/// <summary>Outcome of an action, sent by the agent once it ran or refused it.</summary>
public sealed record AgentActionResult
{
    [EnumDataType(typeof(AgentActionStatus))]
    public required AgentActionStatus Status { get; init; }

    /// <summary>Short plain-text detail for the dashboard.</summary>
    [StringLength(512)]
    public string? Detail { get; init; }
}
