using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Assignment;
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

        endpoints.MapPost(ApiRoutes.AgentInstallReport, InstallReportAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.InstallReport))
            .WithName("AgentInstallReport");

        endpoints.MapGet(ApiRoutes.AgentReleases, ReleasesAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.ReleaseRequest))
            .WithName("AgentReleases");

        endpoints.MapGet(ApiRoutes.AgentActions, ActionsAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.ActionRequest))
            .WithName("AgentActions");

        endpoints.MapPost(ApiRoutes.AgentActionResult, ActionResultAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.ActionResult))
            .WithName("AgentActionResult");

        endpoints.MapPost(ApiRoutes.AgentActionLogs, ActionLogsAsync)
            .RequireAuthorization()
            .AddEndpointFilter(new MessagePolicyFilter(MessageType.ActionResult))
            .WithName("AgentActionLogs");

        return endpoints;
    }

    /// <summary>Accepts a ZIP archive of at most <see cref="AgentActionList.MaxLogBytes"/> for a delivered "UploadLogs" action.</summary>
    internal static async Task<IResult> ActionLogsAsync(
        Guid id,
        HttpContext httpContext,
        AgentActionService actions,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(httpContext.Request.ContentType, "application/zip", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (httpContext.Request.ContentLength > AgentActionList.MaxLogBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Read at most one byte more than allowed, so a body without length header cannot exceed the limit either.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await httpContext.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > AgentActionList.MaxLogBytes)
            {
                return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return await actions.StoreLogsAsync(httpContext.User.GetClientId(), id, buffer.ToArray(), cancellationToken).ConfigureAwait(false) switch
        {
            LogUploadResult.Stored => TypedResults.NoContent(),
            LogUploadResult.Invalid => TypedResults.BadRequest(),
            _ => TypedResults.NotFound(),
        };
    }

    internal static async Task<Ok<AgentActionList>> ActionsAsync(
        HttpContext httpContext,
        AgentActionService actions,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await actions.FetchAsync(httpContext.User.GetClientId(), cancellationToken).ConfigureAwait(false));

    internal static async Task<Results<NoContent, NotFound>> ActionResultAsync(
        Guid id,
        AgentActionResult result,
        HttpContext httpContext,
        AgentActionService actions,
        CancellationToken cancellationToken) =>
        await actions.CompleteAsync(httpContext.User.GetClientId(), id, result, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();

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

    internal static async Task<Results<NoContent, UnauthorizedHttpResult>> InstallReportAsync(
        InstallReport request,
        HttpContext httpContext,
        InstallationService installations,
        CancellationToken cancellationToken)
    {
        var stored = await installations.StoreAsync(httpContext.User.GetClientId(), request, cancellationToken).ConfigureAwait(false);
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
        AssignmentService assignment,
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
        await assignment.RecalculateAsync(client.Id, cancellationToken).ConfigureAwait(false);

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
        GlobalPause globalPause,
        AgentActionService actions,
        CancellationToken cancellationToken)
    {
        var clientId = httpContext.User.GetClientId();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The agent clock is not trusted: a boot time in the future or before 2000 is ignored.
        DateTime? lastBoot = request.LastBootAt is { } boot && boot.UtcDateTime <= now.AddHours(1) && boot.Year >= 2000 ? boot.UtcDateTime : null;

        // Single parameterized UPDATE; no read-modify-write round trip on every heartbeat.
        var updated = await db.Clients
            .Where(c => c.Id == clientId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.LastHeartbeatAtUtc, now)
                    .SetProperty(c => c.AgentVersion, request.AgentVersion)
                    .SetProperty(c => c.OsVersion, c => request.OsVersion ?? c.OsVersion)
                    .SetProperty(c => c.RebootPending, request.RebootPending)
                    .SetProperty(c => c.LastBootAtUtc, c => lastBoot ?? c.LastBootAtUtc),
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

        // Control information (release list version, global pause) only for approved clients (docs/03, 4.2).
        var state = httpContext.User.GetRegistrationState()!.Value;
        if (state != RegistrationState.Approved)
        {
            return TypedResults.Ok(new HeartbeatResponse(state));
        }

        var releasesVersion = await db.Clients.Where(c => c.Id == clientId).Select(c => c.ReleasesVersion).SingleAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new HeartbeatResponse(
            state,
            releasesVersion,
            await globalPause.IsActiveAsync(cancellationToken).ConfigureAwait(false),
            await actions.CountQueuedAsync(clientId, cancellationToken).ConfigureAwait(false)));
    }
}

internal static partial class AgentLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {ClientId} registered from host {HostName}; waiting for approval.")]
    public static partial void Registered(ILogger logger, Guid clientId, string hostName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {ClientId} reset its trust in the central server locally (registry override).")]
    public static partial void TrustReset(ILogger logger, Guid clientId);
}
