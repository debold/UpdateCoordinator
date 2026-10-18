using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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

        endpoints.MapPost(ApiRoutes.AgentInventory, InventoryAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.Inventory))
            .WithMetadata(new RequestSizeLimitAttribute(MaxInventoryBytes))
            .WithName("AgentInventory");

        endpoints.MapPost(ApiRoutes.AgentUpdateReport, UpdateReportAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.UpdateReport))
            .WithMetadata(new RequestSizeLimitAttribute(MaxInventoryBytes))
            .WithName("AgentUpdateReport");

        endpoints.MapGet(ApiRoutes.AgentReleases, ReleasesAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.ReleaseRequest))
            .WithName("AgentReleases");

        return endpoints;
    }

    /// <summary>Upper bound for inventory and update report requests; 5,000 updates with long titles fit comfortably.</summary>
    public const long MaxInventoryBytes = 4 * 1024 * 1024;

    internal static async Task<Results<NoContent, UnauthorizedHttpResult>> UpdateReportAsync(
        UpdateReport request,
        HttpContext httpContext,
        UpdateReportService reports,
        CancellationToken cancellationToken)
    {
        var stored = await reports.StoreAsync(httpContext.User.GetClientId(), request, cancellationToken).ConfigureAwait(false);
        return stored ? TypedResults.NoContent() : TypedResults.Unauthorized();
    }

    internal static async Task<Ok<ReleaseList>> ReleasesAsync(
        HttpContext httpContext,
        UpdateReportService reports,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await reports.GetReleasesAsync(httpContext.User.GetClientId(), cancellationToken).ConfigureAwait(false));

    internal static async Task<Results<NoContent, UnauthorizedHttpResult>> InventoryAsync(
        InventoryRequest request,
        HttpContext httpContext,
        InventoryService inventory,
        CancellationToken cancellationToken)
    {
        var stored = await inventory.StoreAsync(httpContext.User.GetClientId(), request, cancellationToken).ConfigureAwait(false);
        return stored ? TypedResults.NoContent() : TypedResults.Unauthorized();
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
        ILoggerFactory loggerFactory,
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
                    .SetProperty(c => c.AgentVersion, request.AgentVersion)
                    .SetProperty(c => c.OsVersion, c => request.OsVersion ?? c.OsVersion),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            // The client was hard-deleted between authentication and this update.
            return TypedResults.Unauthorized();
        }

        if (request.TrustReset)
        {
            // Security-relevant: will go to the audit log once it exists.
            var logger = loggerFactory.CreateLogger(LogCategory);
            AgentLog.TrustReset(logger, clientId);
        }

        // Control information (release list version) only for approved clients (docs/03, 4.2).
        var state = httpContext.User.GetRegistrationState()!.Value;
        long? releasesVersion = state == RegistrationState.Approved
            ? await db.Clients.Where(c => c.Id == clientId).Select(c => c.ReleasesVersion).SingleAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return TypedResults.Ok(new HeartbeatResponse(state, releasesVersion));
    }
}

internal static partial class AgentLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {ClientId} registered from host {HostName}; waiting for approval.")]
    public static partial void Registered(ILogger logger, Guid clientId, string hostName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {ClientId} reset its trust in the central server locally (registry override).")]
    public static partial void TrustReset(ILogger logger, Guid clientId);
}
