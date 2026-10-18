namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Answer to a heartbeat. Control information is only included for approved clients.
/// </summary>
/// <param name="State">Registration state of the client.</param>
/// <param name="ReleasesVersion">Version of the release list; the agent fetches the list when it changes. Null unless approved.</param>
public sealed record HeartbeatResponse(RegistrationState State, long? ReleasesVersion = null);
