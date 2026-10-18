using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// HTTP endpoints used by agents (see docs/03-communication-protocol.md).
/// </summary>
internal static class AgentEndpoints
{
    public const string RegisterRateLimitPolicy = "agent-register";
    private const string LogCategory = "UpdateCoordinator.Server.Agents";

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ApiRoutes.AgentRegister, RegisterAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RegisterRateLimitPolicy)
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.Register))
            .WithName("RegisterAgent");

        endpoints.MapPost(ApiRoutes.AgentHeartbeat, HeartbeatAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.Heartbeat))
            .WithName("AgentHeartbeat");

        return endpoints;
    }

    internal static async Task<Ok<RegisterResponse>> RegisterAsync(
        RegisterRequest request,
        HttpContext httpContext,
        ServerDbContext db,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var secret = ClientSecrets.Create();
        var client = new ClientEntity
        {
            Id = Guid.NewGuid(),
            SecretHash = ClientSecrets.Hash(secret),
            State = RegistrationState.Pending,
            HostName = request.HostName,
            DomainName = string.IsNullOrEmpty(request.DomainName) ? null : request.DomainName,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            RegisteredFromAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
            RegisteredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var logger = loggerFactory.CreateLogger(LogCategory);
        AgentLog.Registered(logger, client.Id, client.HostName);
        return TypedResults.Ok(new RegisterResponse(client.Id, secret));
    }

    internal static async Task<Results<Ok<HeartbeatResponse>, UnauthorizedHttpResult>> HeartbeatAsync(
        HeartbeatRequest request,
        HttpContext httpContext,
        ServerDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var clientId = httpContext.User.GetClientId();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Single parameterized UPDATE; no read-modify-write round trip on every heartbeat.
        var updated = await db.Clients
            .Where(c => c.Id == clientId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.LastHeartbeatAtUtc, now)
                    .SetProperty(c => c.AgentVersion, request.AgentVersion),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            // The client was hard-deleted between authentication and this update.
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(new HeartbeatResponse(httpContext.User.GetRegistrationState()!.Value));
    }
}

internal static partial class AgentLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {ClientId} registered from host {HostName}; waiting for approval.")]
    public static partial void Registered(ILogger logger, Guid clientId, string hostName);
}
