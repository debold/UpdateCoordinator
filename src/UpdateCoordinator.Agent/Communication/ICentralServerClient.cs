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

    /// <summary>Sends the result of an update run. Returns false if the central server no longer accepts the credentials.</summary>
    Task<bool> SendUpdateReportAsync(AgentIdentity identity, UpdateReport report, CancellationToken cancellationToken);

    /// <summary>Fetches the released updates. Returns null if the central server no longer accepts the credentials.</summary>
    Task<ReleaseList?> GetReleasesAsync(AgentIdentity identity, CancellationToken cancellationToken);

    /// <summary>Sends the result of an installation run. Returns false if the central server no longer accepts the credentials.</summary>
    Task<bool> SendInstallReportAsync(AgentIdentity identity, InstallReport report, CancellationToken cancellationToken);

    /// <summary>Fetches the queued remote actions. Returns null if the central server no longer accepts the credentials.</summary>
    Task<AgentActionList?> GetActionsAsync(AgentIdentity identity, CancellationToken cancellationToken);

    /// <summary>Sends the result of a remote action. Returns false if the central server no longer accepts the credentials.</summary>
    Task<bool> SendActionResultAsync(AgentIdentity identity, Guid actionId, AgentActionResult result, CancellationToken cancellationToken);

    /// <summary>Uploads a log archive for a remote action. Returns false if the central server no longer accepts the credentials.</summary>
    Task<bool> UploadLogsAsync(AgentIdentity identity, Guid actionId, byte[] archive, CancellationToken cancellationToken);

    /// <summary>Requests a lease for a coordinated group. Returns null if the central server no longer accepts the credentials.</summary>
    Task<LeaseResponse?> AcquireLeaseAsync(AgentIdentity identity, CancellationToken cancellationToken);

    /// <summary>Releases the lease with its outcome. Returns false if the central server no longer accepts the credentials.</summary>
    Task<bool> ReleaseLeaseAsync(AgentIdentity identity, bool succeeded, CancellationToken cancellationToken);
}
