namespace UpdateCoordinator.Contracts;

/// <summary>
/// All HTTP routes of the central server in one place, so agent, server and tests share the same paths.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Liveness endpoint of the central server for monitoring; no authentication, no payload.</summary>
    public const string Health = "/health";

    /// <summary>Registration of a new agent; the only agent endpoint that does not require client credentials.</summary>
    public const string AgentRegister = "/api/v1/agents/register";

    /// <summary>Periodic heartbeat of a registered agent.</summary>
    public const string AgentHeartbeat = "/api/v1/agents/heartbeat";

    /// <summary>Full inventory of a registered agent (also accepted before approval).</summary>
    public const string AgentInventory = "/api/v1/agents/inventory";

    /// <summary>Result of an update search of an approved agent.</summary>
    public const string AgentUpdateReport = "/api/v1/agents/updates";

    /// <summary>Updates released for an approved agent.</summary>
    public const string AgentReleases = "/api/v1/agents/releases";

    /// <summary>Signed binding between trust anchor and TLS certificate; public, self-authenticating data.</summary>
    public const string TrustBinding = "/api/v1/trust/binding";
}
