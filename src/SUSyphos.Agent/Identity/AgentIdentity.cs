namespace SUSyphos.Agent.Identity;

/// <summary>
/// Credentials issued by the central server at registration.
/// </summary>
internal sealed record AgentIdentity(Guid ClientId, string ClientSecret);
