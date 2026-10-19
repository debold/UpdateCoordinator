namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Answer to a heartbeat. Control information is only included for approved clients.
/// </summary>
/// <param name="State">Registration state of the client.</param>
/// <param name="ReleasesVersion">Version of the release list; the agent fetches the list when it changes. Null unless approved.</param>
/// <param name="InstallationsPaused">Global pause: no new installations start; check, search and reporting continue.</param>
/// <param name="PendingActions">Number of remote actions waiting; the agent fetches them when it is above zero.</param>
public sealed record HeartbeatResponse(RegistrationState State, long? ReleasesVersion = null, bool InstallationsPaused = false, int PendingActions = 0);
