namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Answer to a lease request of a server in a coordinated group (docs/04-central-server.md, group coordination).
/// A lease allows installing and rebooting until it expires or is released.
/// </summary>
/// <param name="Granted">True if the server may start now.</param>
/// <param name="ExpiresAt">End of the lease when granted.</param>
/// <param name="Reason">Why the lease was not granted (all leases taken, group stopped).</param>
public sealed record LeaseResponse(bool Granted, DateTimeOffset? ExpiresAt = null, string? Reason = null);

/// <summary>Ends the lease of the server; the outcome feeds the quality gate of the group.</summary>
public sealed record LeaseRelease
{
    /// <summary>True if installation (and reboot) succeeded and the server is healthy.</summary>
    public required bool Succeeded { get; init; }
}
