namespace UpdateCoordinator.Server.Data;

internal enum RolloutState
{
    /// <summary>The rollout runs through the rings.</summary>
    Active = 0,

    /// <summary>Stopped by an operator: nothing is released until it is started again.</summary>
    Stopped = 1,
}

/// <summary>
/// The rollout of one update (ID and revision) through the rings (docs/04-central-server.md, section 3).
/// An update without a rollout is "queued": known from reports but not released anywhere.
/// </summary>
internal sealed class RolloutEntity
{
    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public RolloutState State { get; set; }

    public DateTime StartedAtUtc { get; set; }

    /// <summary>Account that started the rollout, or "rule" for an automatic start.</summary>
    public required string StartedBy { get; set; }

    /// <summary>Manual override: released to every ring at once, bypassing rates, wait times and gates.</summary>
    public bool Override { get; set; }
}

internal enum RingRolloutState
{
    /// <summary>The previous ring has not met the transition rule yet.</summary>
    Waiting = 0,

    /// <summary>Released for the servers of this ring.</summary>
    Released = 1,

    /// <summary>The transition rule is met, but the transition has a manual gate.</summary>
    AwaitingApproval = 2,

    /// <summary>The previous ring has a poor success rate or no feedback at the maximum wait time; a human decides.</summary>
    Alarm = 3,
}

/// <summary>
/// State of one rollout in one ring. Rates, wait times and transitions apply per update and ring.
/// </summary>
internal sealed class RolloutRingEntity
{
    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public int RingId { get; set; }

    public RingRolloutState State { get; set; }

    /// <summary>When the update was released for this ring; starts the wait time for the next transition.</summary>
    public DateTime? ReleasedAtUtc { get; set; }

    public DateTime StateChangedAtUtc { get; set; }

    /// <summary>Why the ring waits for a human (alarm reason), shown in the dashboard.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Rule for the transition from one ring to the next (Pilot → Test, Test → Production).
/// </summary>
internal sealed class RingTransitionEntity
{
    public const int MaxWaitDays = 90;

    /// <summary>Ring the transition starts from; the target is the next ring by order.</summary>
    public int FromRingId { get; set; }

    /// <summary>Minimum time between the release in the source ring and the transition.</summary>
    public TimeSpan MinimumWait { get; set; }

    /// <summary>After this time the rollout proceeds on a good success rate even if the feedback rate was missed.</summary>
    public TimeSpan MaximumWait { get; set; }

    /// <summary>Share of targeted servers that reported a result, in percent.</summary>
    public int MinimumFeedbackPercent { get; set; }

    /// <summary>Share of successful results among the received results, in percent.</summary>
    public int MinimumSuccessPercent { get; set; }

    /// <summary>A human must approve the transition when the rule is met.</summary>
    public bool ManualGate { get; set; }
}

internal enum StartMode
{
    /// <summary>The rollout starts in the first ring by itself.</summary>
    Automatic = 0,

    /// <summary>The update stays queued until an operator starts it.</summary>
    Manual = 1,
}

/// <summary>
/// Decides how a newly reported update enters the first ring. The matching rule with the lowest priority number applies;
/// all set conditions must match, a rule without conditions matches every update. Without a matching rule the update stays queued.
/// </summary>
internal sealed class StartRuleEntity
{
    public const int CatchAllId = 1;

    public const int MaxDelayHours = 720;

    public int Id { get; set; }

    public int Priority { get; set; }

    /// <summary>Update classification as reported by Windows Update, e.g. "Security Updates".</summary>
    public string? Classification { get; set; }

    /// <summary>MSRC severity, e.g. "Critical".</summary>
    public string? MsrcSeverity { get; set; }

    /// <summary>Title pattern with * and ? wildcards, e.g. "*Cumulative Update*".</summary>
    public string? TitlePattern { get; set; }

    public StartMode Mode { get; set; }

    /// <summary>Delay between the start of the rollout and the earliest installation in the first ring.</summary>
    public TimeSpan Delay { get; set; }
}

/// <summary>Patch compliance of one day: approved servers and those with every released update installed.</summary>
internal sealed class ComplianceSnapshotEntity
{
    public DateOnly Day { get; set; }

    public int Servers { get; set; }

    public int Compliant { get; set; }
}

internal enum AgentActionState
{
    Queued = 0,
    Delivered = 1,
    Succeeded = 2,
    Failed = 3,
    Rejected = 4,
    Cancelled = 5,
    Expired = 6,
}

/// <summary>A remote action queued for one server, with its progress (docs/04-central-server.md, entity "Remote action").</summary>
internal sealed class AgentActionEntity
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public UpdateCoordinator.Contracts.Agents.AgentActionKind Kind { get; set; }

    public AgentActionState State { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    public required string RequestedBy { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? Detail { get; set; }
}
