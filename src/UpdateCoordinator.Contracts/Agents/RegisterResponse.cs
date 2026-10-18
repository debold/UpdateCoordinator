namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Credentials issued by the central server on registration. The agent sends both with every further
/// message (HTTP Basic authentication). The secret is returned only once and never stored in plain text on the server.
/// </summary>
public sealed record RegisterResponse(Guid ClientId, string ClientSecret);
