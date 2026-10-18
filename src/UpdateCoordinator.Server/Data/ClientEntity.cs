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
}
