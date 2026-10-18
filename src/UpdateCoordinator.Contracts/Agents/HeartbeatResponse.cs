namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Answer to a heartbeat. Control information is added only for approved clients in later features.
/// </summary>
public sealed record HeartbeatResponse(RegistrationState State);
