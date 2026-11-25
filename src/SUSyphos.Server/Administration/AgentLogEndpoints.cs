using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Administration;

/// <summary>
/// The "Download logs" button of a server for one user: the newest uploaded logs, when this user downloaded them, and
/// a newer request that is still open. The page renders it, the dashboard script polls it, so the button turns blue
/// when new logs arrive without a reload (Marc, 2026-11-23).
/// </summary>
internal sealed record AgentLogsState(
    [property: System.Text.Json.Serialization.JsonPropertyName("href")] string? Href,
    [property: System.Text.Json.Serialization.JsonPropertyName("downloaded")] bool Downloaded,
    [property: System.Text.Json.Serialization.JsonPropertyName("requested")] bool Requested,
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title)
{
    public static async Task<AgentLogsState> LoadAsync(ServerDbContext db, Guid clientId, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var uploads = await db.AgentActions.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.Kind == SUSyphos.Contracts.Agents.AgentActionKind.UploadLogs)
            .Select(a => new { a.Id, a.State, a.LogFileSize, a.RequestedAtUtc, a.CompletedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var latest = uploads.Where(a => a.LogFileSize != null).MaxBy(a => a.CompletedAtUtc);
        var newer = uploads.Where(a => a.State is AgentActionState.Queued or AgentActionState.Delivered && a.RequestedAtUtc > (latest?.CompletedAtUtc ?? DateTime.MinValue)).MaxBy(a => a.RequestedAtUtc);
        if (latest is null)
        {
            return new AgentLogsState(null, false, newer is not null, newer is null ? "No logs uploaded yet" : $"New logs requested {Components.DisplayTime.Format(newer.RequestedAtUtc)}");
        }

        var id = latest.Id.ToString("D");
        var downloadedAt = await db.AuditEntries.AsNoTracking().Where(a => a.Action == "Action.LogsDownloaded" && a.Actor == user && a.Details == id).MaxAsync(a => (DateTime?)a.AtUtc, cancellationToken).ConfigureAwait(false);
        var title = $"Logs the agent uploaded on {Components.DisplayTime.Format(latest.CompletedAtUtc ?? latest.RequestedAtUtc)}"
            + (downloadedAt is { } at ? $"; you downloaded them on {Components.DisplayTime.Format(at)}" : "; not downloaded by you yet")
            + (newer is null ? string.Empty : $"; new logs requested {Components.DisplayTime.Format(newer.RequestedAtUtc)}, the button turns blue when they are here");
        return new AgentLogsState(AgentLogEndpoints.DownloadPath(latest.Id), downloadedAt is not null, newer is not null, title);
    }
}

/// <summary>Download of agent log archives from the dashboard (Operator role, audited).</summary>
internal static class AgentLogEndpoints
{
    public const string DownloadRoute = "/administration/agent-logs/{id:guid}";

    public static string DownloadPath(Guid actionId) => $"/administration/agent-logs/{actionId:D}";

    /// <summary>The state of the "Download logs" button of a server for the signed-in user (JSON for the dashboard script).</summary>
    public const string StateRoute = "/administration/agent-logs/state/{clientId:guid}";

    public static string StatePath(Guid clientId) => $"/administration/agent-logs/state/{clientId:D}";

    public static IEndpointRouteBuilder MapAgentLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DownloadRoute, DownloadAsync)
            .RequireAuthorization(AdminRoles.Policies.Operate)
            .WithName("AgentLogDownload");
        endpoints.MapGet(StateRoute, async (Guid clientId, HttpContext httpContext, ServerDbContext db, CancellationToken cancellationToken) =>
                Results.Json(await AgentLogsState.LoadAsync(db, clientId, httpContext.User.Identity?.Name ?? "unknown", cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(AdminRoles.Policies.Operate);
        return endpoints;
    }

    internal static async Task<IResult> DownloadAsync(
        Guid id,
        HttpContext httpContext,
        ServerDbContext db,
        AgentActionService actions,
        AuditLog audit,
        CancellationToken cancellationToken)
    {
        var action = await db.AgentActions.AsNoTracking()
            .Where(a => a.Id == id && a.LogFileSize != null)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a.Id, a.RequestedAtUtc, c.HostName })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var path = actions.LogPath(id);
        if (action is null || !File.Exists(path))
        {
            return TypedResults.NotFound();
        }

        await audit.WriteAsync(httpContext.User.Identity?.Name ?? "unknown", "Action.LogsDownloaded", action.HostName, id.ToString("D"), cancellationToken).ConfigureAwait(false);

        // The host name passed validation (letters, digits, hyphen), so it is safe in the file name.
        var fileName = $"agent-logs-{action.HostName}-{action.RequestedAtUtc:yyyyMMdd-HHmm}.zip";
        return TypedResults.PhysicalFile(path, "application/zip", fileName);
    }
}
