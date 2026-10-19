using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Server.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Administration;

/// <summary>Download of agent log archives from the dashboard (Operator role, audited).</summary>
internal static class AgentLogEndpoints
{
    public const string DownloadRoute = "/administration/agent-logs/{id:guid}";

    public static string DownloadPath(Guid actionId) => $"/administration/agent-logs/{actionId:D}";

    public static IEndpointRouteBuilder MapAgentLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DownloadRoute, DownloadAsync)
            .RequireAuthorization(AdminRoles.Policies.Operate)
            .WithName("AgentLogDownload");
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
