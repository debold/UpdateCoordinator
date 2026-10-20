using SUSyphos.Contracts.Agents;

namespace SUSyphos.Server.Data;

/// <summary>
/// A registered agent. Minimal schema for the register/heartbeat slice; extended by later features.
/// </summary>
internal sealed class ClientEntity
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hash of the client secret. The secret itself is never stored.</summary>
    public required byte[] SecretHash { get; set; }

    public RegistrationState State { get; set; }

    public required string HostName { get; set; }

    public string? DomainName { get; set; }

    public required string OsVersion { get; set; }

    public required string AgentVersion { get; set; }

    /// <summary>Why the last agent self-update failed, as reported by the agent; null when it succeeded or none was attempted.</summary>
    public string? AgentUpdateError { get; set; }

    /// <summary>Remote IP address seen at registration, shown to the admin for manual approval.</summary>
    public string? RegisteredFromAddress { get; set; }

    public DateTime RegisteredAtUtc { get; set; }

    public DateTime? LastHeartbeatAtUtc { get; set; }

    // Inventory (see docs/05-agent-design.md, section 2); empty until the first inventory arrives.

    public string? OsProductName { get; set; }

    public string? OsEditionId { get; set; }

    public string? OsDisplayVersion { get; set; }

    /// <summary>Detected roles, e.g. DomainController; stored as a JSON array.</summary>
    public List<string> Roles { get; set; } = [];

    public bool UsesWsus { get; set; }

    public string? WsusServer { get; set; }

    /// <summary>Windows Automatic Updates mode from the inventory; null until an agent 0.3 or later reports it.</summary>
    public AutomaticUpdateMode? AutoUpdateMode { get; set; }

    public bool AutomaticUpdatesByPolicy { get; set; }

    /// <summary>The agent itself set Automatic Updates to option 3 on request of the central server.</summary>
    public bool AutomaticUpdatesManagedByAgent { get; set; }

    /// <summary>Windows installs or restarts on its own (scheduled install, scheduled restart or compliance deadline).</summary>
    public bool AutomaticUpdatesBypass { get; set; }

    public DateTime? InventoryAtUtc { get; set; }

    // Update runs (see docs/05-agent-design.md, section 3).

    public DateTime? LastSearchAtUtc { get; set; }

    public bool? LastSearchSucceeded { get; set; }

    public int? LastSearchHResult { get; set; }

    public List<HealthCheckEntry> HealthChecks { get; set; } = [];

    /// <summary>Incremented whenever the releases of this client change; announced in the heartbeat.</summary>
    public long ReleasesVersion { get; set; }

    public DateTime? LastInstallAtUtc { get; set; }

    /// <summary>Reboot pending according to the last heartbeat (Windows registry) or installation run.</summary>
    public bool RebootPending { get; set; }

    /// <summary>Why the pending reboot waits, as decided by the agent; null if no reboot is pending or the agent does not report it.</summary>
    public RebootWait? RebootWait { get; set; }

    /// <summary>The agent holds an administrator's request to restart in the maintenance window.</summary>
    public bool RestartRequested { get; set; }

    /// <summary>When the agent plans the requested restart; null if unknown.</summary>
    public DateTime? PlannedRestartAtUtc { get; set; }

    // Live status (docs/05-agent-design.md, section 5); see Agents.LiveStatus.

    public AgentPhase? ActivityPhase { get; set; }

    public int? ActivityPercent { get; set; }

    /// <summary>Updates of the current download or installation; stored as a JSON array.</summary>
    public List<Guid> ActivityUpdateIds { get; set; } = [];

    public DateTime? ActivityStartedAtUtc { get; set; }

    /// <summary>When the activity was last reported; older than LiveStatus.StaleAfter means it is not current any more.</summary>
    public DateTime? ActivityReportedAtUtc { get; set; }

    /// <summary>Last start of the operating system reported by the agent.</summary>
    public DateTime? LastBootAtUtc { get; set; }

    /// <summary>Host name of the last heartbeat; with the previous one it reveals copied machines (clone suspicion).</summary>
    public string? LastHeartbeatHostName { get; set; }

    public string? PreviousHostName { get; set; }

    public DateTime? PreviousHostNameSeenAtUtc { get; set; }

    /// <summary>Windows time zone of the server from the inventory; null if unknown.</summary>
    public string? TimeZoneId { get; set; }

    // Assignment (docs/04-central-server.md, section 3).

    /// <summary>Effective ring: manual override, matching ring rule or the fallback ring Production.</summary>
    public int? RingId { get; set; }

    /// <summary>Manual ring override; null means automatic assignment.</summary>
    public int? ManualRingId { get; set; }

    /// <summary>Server specific maintenance windows as JSON; null uses the windows of its groups or the system default.</summary>
    public string? MaintenanceWindowsJson { get; set; }

    /// <summary>Effective maintenance windows delivered to the agent (JSON).</summary>
    public string? EffectiveWindowsJson { get; set; }

    /// <summary>Server specific reboot policy; null uses the policy of its groups or the system default.</summary>
    public SUSyphos.Contracts.Agents.RebootPolicy? RebootPolicy { get; set; }

    /// <summary>Effective reboot policy delivered to the agent.</summary>
    public SUSyphos.Contracts.Agents.RebootPolicy EffectiveRebootPolicy { get; set; }

    /// <summary>Effective setting delivered to the agent: keep Windows from installing updates on its own (docs/05-agent-design.md, section 3).</summary>
    public bool EffectiveManageAutomaticUpdates { get; set; }

    /// <summary>Effective setting delivered to the agent: search and install through Microsoft Update as well.</summary>
    public bool EffectiveUseMicrosoftUpdate { get; set; }

    /// <summary>The server is in at least one coordinated group and needs a lease before installing or rebooting.</summary>
    public bool RequiresLease { get; set; }
}
