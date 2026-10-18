using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Server.Data;

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

    public DateTime? InventoryAtUtc { get; set; }

    // Update runs (see docs/05-agent-design.md, section 3).

    public DateTime? LastSearchAtUtc { get; set; }

    public bool? LastSearchSucceeded { get; set; }

    public int? LastSearchHResult { get; set; }

    public List<HealthCheckEntry> HealthChecks { get; set; } = [];

    /// <summary>Incremented whenever the releases of this client change; announced in the heartbeat.</summary>
    public long ReleasesVersion { get; set; }

    public DateTime? LastInstallAtUtc { get; set; }

    /// <summary>Reboot needed according to the latest installation run (the agent never reboots on its own).</summary>
    public bool RebootPending { get; set; }

    // Assignment (docs/04-central-server.md, section 3).

    /// <summary>Effective ring: manual override, matching ring rule or the default ring.</summary>
    public int? RingId { get; set; }

    /// <summary>Manual ring override; null means automatic assignment.</summary>
    public int? ManualRingId { get; set; }

    /// <summary>Server specific maintenance windows as JSON; null uses the windows of its groups or the system default.</summary>
    public string? MaintenanceWindowsJson { get; set; }

    /// <summary>Effective maintenance windows delivered to the agent (JSON).</summary>
    public string? EffectiveWindowsJson { get; set; }
}
