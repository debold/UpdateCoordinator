namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Answer to a heartbeat. Control information is only included for approved clients.
/// </summary>
/// <param name="State">Registration state of the client.</param>
/// <param name="ReleasesVersion">Version of the release list; the agent fetches the list when it changes. Null unless approved.</param>
/// <param name="InstallationsPaused">Global pause: no new installations start; check, search and reporting continue.</param>
/// <param name="PendingActions">Number of remote actions waiting; the agent fetches them when it is above zero.</param>
/// <param name="AgentUpdate">A newer agent version released for the server's ring; null if the agent is current.</param>
public sealed record HeartbeatResponse(RegistrationState State, long? ReleasesVersion = null, bool InstallationsPaused = false, int PendingActions = 0, AgentUpdate? AgentUpdate = null);

/// <summary>
/// An agent package to update to (docs/05-agent-design.md, section 7). The agent downloads it from the central server
/// (<see cref="ApiRoutes.AgentPackageFor"/>) and installs it only if size and SHA-256 match.
/// </summary>
/// <param name="Version">Semantic version of the package.</param>
/// <param name="Sha256">SHA-256 of the MSI as lowercase hex.</param>
/// <param name="Size">Size of the MSI in bytes.</param>
public sealed record AgentUpdate(string Version, string Sha256, long Size);
