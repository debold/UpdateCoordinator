namespace UpdateCoordinator.Contracts;

/// <summary>
/// All HTTP routes of the central server in one place, so agent, server and tests share the same paths.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Liveness endpoint of the central server for monitoring; no authentication, no payload.</summary>
    public const string Health = "/health";
}
