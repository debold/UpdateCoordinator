namespace SUSyphos.Server.Data;

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

    /// <summary>Stopped automatically because the update looked withdrawn by Microsoft; resumed by itself if that turns out wrong. A manual stop or resume clears it.</summary>
    public bool StoppedAsWithdrawn { get; set; }

    /// <summary>Start rule that started the rollout automatically; null for a manual start. No foreign key: the rule may be deleted later.</summary>
    public int? StartRuleId { get; set; }

    /// <summary>Description of the start rule at the time it started the rollout (priority, conditions, rings), so later changes to the rule do not rewrite history.</summary>
    public string? StartRuleSummary { get; set; }

    /// <summary>
    /// Servers may install the update outside their maintenance windows (start rule for updates that never restart).
    /// Delivered only while Windows reports the update as never restarting.
    /// </summary>
    public bool OutsideWindows { get; set; }
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

    public const int MaxTitlePatternLength = 128;

    public StartMode Mode { get; set; }

    /// <summary>Delay between the start of the rollout and the earliest installation in the first ring.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>
    /// Rings the update is released in right away, comma-separated ring IDs; null means the first ring only. The rings in
    /// between still follow their transition rules, e.g. definition updates can start in every ring at once.
    /// </summary>
    public string? StartRingIds { get; set; }

    /// <summary>Condition on the restart behaviour Windows Update reports for the update (<see cref="SUSyphos.Contracts.Agents.RebootBehavior"/>); null matches any.</summary>
    public int? RebootBehavior { get; set; }

    /// <summary>Updates started by this rule may install outside the maintenance windows; only with the condition "never restarts".</summary>
    public bool OutsideWindows { get; set; }
}

/// <summary>
/// The state of one day for the overview statistics: approved servers, those with every released update installed
/// (patch compliance), those missing no update at all (fully patched), servers by status and open server × update pairs.
/// Recorded every worker cycle; the last record of a day stays.
/// </summary>
internal sealed class ComplianceSnapshotEntity
{
    public DateOnly Day { get; set; }

    public int Servers { get; set; }

    public int Compliant { get; set; }

    /// <summary>When the day was last recorded (the worker records every 15 minutes).</summary>
    public DateTime? RecordedAtUtc { get; set; }

    /// <summary>Servers whose update search reports no missing update, released or not; null before 0.7.7.</summary>
    public int? FullyPatched { get; set; }

    public int? StatusOk { get; set; }

    public int? StatusUpdating { get; set; }

    public int? StatusRebootPending { get; set; }

    public int? StatusWarning { get; set; }

    public int? StatusFailed { get; set; }

    public int? StatusOffline { get; set; }

    /// <summary>Server × update pairs released and not installed yet.</summary>
    public int? OpenReleased { get; set; }

    /// <summary>Server × update pairs reported missing but not released to the server's ring yet.</summary>
    public int? OpenHeld { get; set; }

    /// <summary>Servers on the security baseline of their Windows version (MSRC); null without MSRC data or before 0.7.9.</summary>
    public int? BaselineCurrent { get; set; }

    /// <summary>Servers the baseline can be checked for (known Windows Server version and MSRC data).</summary>
    public int? BaselineKnown { get; set; }

    /// <summary>Open server × vulnerability pairs (the server build is older than the fixing build).</summary>
    public int? OpenVulnerabilities { get; set; }

    /// <summary>Of those, vulnerabilities Microsoft knows to be exploited.</summary>
    public int? OpenExploited { get; set; }
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

    public SUSyphos.Contracts.Agents.AgentActionKind Kind { get; set; }

    public AgentActionState State { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    public required string RequestedBy { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? Detail { get; set; }

    /// <summary>Size of the uploaded log archive while it is kept; null if there is none (any more).</summary>
    public long? LogFileSize { get; set; }

    /// <summary>The update an action is about (uninstall); null for other actions.</summary>
    public Guid? UpdateId { get; set; }

    public int? Revision { get; set; }

    /// <summary>KB number of that update, so the agent can find the package without Windows Update (DISM).</summary>
    public string? KbArticle { get; set; }
}

internal enum LeaseState
{
    Active = 0,
    Succeeded = 1,
    Failed = 2,
    Expired = 3,
}

/// <summary>Permission of one server to install and reboot inside a coordinated group (docs/04-central-server.md, leases).</summary>
internal sealed class LeaseEntity
{
    public long Id { get; set; }

    public int GroupId { get; set; }

    public Guid ClientId { get; set; }

    public LeaseState State { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }
}
