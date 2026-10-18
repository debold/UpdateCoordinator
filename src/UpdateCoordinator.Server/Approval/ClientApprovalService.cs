using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Approval;

internal enum ApprovalResult
{
    Done,
    NotFound,
    NotPending,
}

/// <summary>
/// Approval decisions for newly registered clients (docs/03-communication-protocol.md, section 4.1).
/// Only pending clients can be decided; every decision is written to the audit log.
/// </summary>
internal sealed partial class ClientApprovalService(ServerDbContext db, AuditLog audit, ILogger<ClientApprovalService> logger)
{
    public const string RuleActor = "rule";

    public Task<ApprovalResult> ApproveAsync(Guid clientId, string actor, string? reason, CancellationToken cancellationToken) =>
        DecideAsync(clientId, RegistrationState.Approved, actor, reason, cancellationToken);

    public Task<ApprovalResult> RejectAsync(Guid clientId, string actor, string? reason, CancellationToken cancellationToken) =>
        DecideAsync(clientId, RegistrationState.Rejected, actor, reason, cancellationToken);

    private async Task<ApprovalResult> DecideAsync(Guid clientId, RegistrationState decision, string actor, string? reason, CancellationToken cancellationToken)
    {
        // Single conditional UPDATE: a client is decided at most once, even with concurrent requests.
        var changed = await db.Clients
            .Where(c => c.Id == clientId && c.State == RegistrationState.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, decision), cancellationToken)
            .ConfigureAwait(false);

        if (changed == 0)
        {
            return await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false)
                ? ApprovalResult.NotPending
                : ApprovalResult.NotFound;
        }

        var host = await db.Clients.Where(c => c.Id == clientId).Select(c => c.HostName).SingleAsync(cancellationToken).ConfigureAwait(false);
        var action = decision == RegistrationState.Approved ? "Client.Approved" : "Client.Rejected";
        await audit.WriteAsync(actor, action, $"{host} ({clientId:D})", reason, cancellationToken).ConfigureAwait(false);
        LogDecision(clientId, host, decision, actor);
        return ApprovalResult.Done;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {ClientId} ({HostName}) set to {State} by {Actor}.")]
    private partial void LogDecision(Guid clientId, string hostName, RegistrationState state, string actor);
}
