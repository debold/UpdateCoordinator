using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Administration;

/// <summary>Health of a server as the dashboard shows it, worst state first.</summary>
internal enum ServerHealthState
{
    NotApproved,
    Offline,
    Failed,
    RebootPending,
    Warning,
    Ok,
}

/// <summary>
/// Classifies servers for the dashboard (docs/06-dashboard.md, sections 2 and 4). One place, so overview, server list
/// and server detail agree.
/// </summary>
internal static class ServerHealth
{
    /// <summary>An approved server without heartbeat for this long counts as offline.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromHours(1);

    /// <param name="client">The server.</param>
    /// <param name="hasFailedInstallation">The last installation of an update that is still released failed.</param>
    /// <param name="nowUtc">Current time.</param>
    public static ServerHealthState Evaluate(ClientEntity client, bool hasFailedInstallation, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.State != RegistrationState.Approved)
        {
            return ServerHealthState.NotApproved;
        }

        if (client.LastHeartbeatAtUtc is not { } heartbeat || nowUtc - heartbeat > OfflineAfter)
        {
            return ServerHealthState.Offline;
        }

        if (hasFailedInstallation || client.HealthChecks.Any(c => c.Status == (int)HealthStatus.Failed))
        {
            return ServerHealthState.Failed;
        }

        if (client.RebootPending)
        {
            return ServerHealthState.RebootPending;
        }

        return client.HealthChecks.Any(c => c.Status == (int)HealthStatus.Warning) || client.WsusServer is not null
            ? ServerHealthState.Warning
            : ServerHealthState.Ok;
    }

    public static string Label(ServerHealthState state) => state switch
    {
        ServerHealthState.NotApproved => "Not approved",
        ServerHealthState.Offline => "Offline",
        ServerHealthState.Failed => "Failed",
        ServerHealthState.RebootPending => "Reboot pending",
        ServerHealthState.Warning => "Warning",
        _ => "OK",
    };

    /// <summary>CSS class of the status pill.</summary>
    public static string PillClass(ServerHealthState state) => state switch
    {
        ServerHealthState.Failed or ServerHealthState.Offline => "fail",
        ServerHealthState.RebootPending or ServerHealthState.Warning => "queue",
        ServerHealthState.NotApproved => "neutral",
        _ => "ok",
    };
}
