using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Communication;

/// <summary>
/// Messages the agent sends to the central server.
/// </summary>
internal interface ICentralServerClient
{
    Task<AgentIdentity> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);

    /// <summary>Sends a heartbeat. Returns null if the central server no longer accepts the credentials.</summary>
    Task<HeartbeatResponse?> HeartbeatAsync(AgentIdentity identity, HeartbeatRequest request, CancellationToken cancellationToken);

    /// <summary>Sends the inventory. Returns false if the central server no longer accepts the credentials.</summary>
    Task<bool> SendInventoryAsync(AgentIdentity identity, InventoryRequest request, CancellationToken cancellationToken);
}
