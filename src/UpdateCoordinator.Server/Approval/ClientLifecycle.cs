using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Approval;

/// <summary>
/// Removing servers and clone suspicions (docs/04-central-server.md, removing servers).
/// Soft delete keeps the history and invalidates the credentials; a still running agent registers again as a new pending
/// server. Hard delete removes the server with all its data. A clone suspicion (the same credentials used by two hosts
/// alternately) blocks the server until an administrator decides.
/// </summary>
internal sealed partial class ClientLifecycle(ServerDbContext db, AuditLog audit, TimeProvider timeProvider, ILogger<ClientLifecycle> logger)
{
    /// <summary>Two host names alternating within this time count as a clone suspicion; a single rename does not.</summary>
    public static readonly TimeSpan CloneWindow = TimeSpan.FromHours(1);

    public async Task<Outcome> SoftDeleteAsync(Guid clientId, string actor, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null || client.State == RegistrationState.Deleted)
        {
            return new Outcome("The server no longer exists or is already deleted.");
        }

        client.State = RegistrationState.Deleted;

        // Unknown random secret: the agent's credentials stop working, it registers again as a new pending server.
        client.SecretHash = RandomNumberGenerator.GetBytes(32);
        await db.Releases.Where(r => r.ClientId == clientId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.AgentActions.Where(a => a.ClientId == clientId && (a.State == AgentActionState.Queued || a.State == AgentActionState.Delivered))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentActionState.Cancelled), cancellationToken).ConfigureAwait(false);
        await db.Leases.Where(l => l.ClientId == clientId && l.State == LeaseState.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.State, LeaseState.Succeeded).SetProperty(l => l.EndedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Client.SoftDeleted", $"{client.HostName} ({clientId:D})", null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> HardDeleteAsync(Guid clientId, string actor, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server no longer exists.");
        }

        // Releases, history, memberships, actions and leases are removed with the server by the database.
        await db.Clients.Where(c => c.Id == clientId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Client.HardDeleted", $"{client.HostName} ({clientId:D})", null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Ends a clone suspicion: the server is approved again (the administrator checked that only one host uses it).</summary>
    public async Task<Outcome> UnblockAsync(Guid clientId, string actor, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId && c.State == RegistrationState.Blocked, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server is not blocked.");
        }

        client.State = RegistrationState.Approved;
        client.PreviousHostName = null;
        client.PreviousHostNameSeenAtUtc = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Client.Unblocked", $"{client.HostName} ({clientId:D})", null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>
    /// Records the host name a heartbeat came from. When a client's credentials are used by two host names alternately
    /// within <see cref="CloneWindow"/>, the client is blocked: most likely a copied virtual machine.
    /// Returns true if the client was blocked now.
    /// </summary>
    public async Task<bool> RecordHostAsync(Guid clientId, string hostName, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null || string.Equals(client.LastHeartbeatHostName, hostName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var alternating = client.LastHeartbeatHostName is not null
            && string.Equals(client.PreviousHostName, hostName, StringComparison.OrdinalIgnoreCase)
            && client.PreviousHostNameSeenAtUtc is { } seen && now - seen < CloneWindow;

        client.PreviousHostName = client.LastHeartbeatHostName;
        client.PreviousHostNameSeenAtUtc = client.LastHeartbeatHostName is null ? null : now;
        client.LastHeartbeatHostName = hostName;

        if (alternating && client.State is RegistrationState.Approved or RegistrationState.Pending)
        {
            client.State = RegistrationState.Blocked;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditLog.SystemActor, "Client.CloneSuspected", $"{client.HostName} ({clientId:D})", $"heartbeats from {client.PreviousHostName} and {hostName}", cancellationToken).ConfigureAwait(false);
            LogCloneSuspected(clientId, client.PreviousHostName!, hostName);
            return true;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId} blocked: heartbeats alternate between hosts {First} and {Second} (clone suspicion).")]
    private partial void LogCloneSuspected(Guid clientId, string first, string second);
}
