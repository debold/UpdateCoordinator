using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Administration;

/// <summary>One remote action the signed-in user requested, as "My requests" shows it.</summary>
internal sealed record MyRequest(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("serverLink")] string ServerLink,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("stateClass")] string StateClass,
    [property: JsonPropertyName("open")] bool Open,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("requested")] string Requested,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("download")] string? Download);

/// <summary>
/// "My requests" (Marc, 2026-11-20, mockup https://claude.ai/artifact/FGj8ZLe3fiMivdb1EogC8L, variants 1 and 2): the remote
/// actions the signed-in user requested, open ones and those finished within the last hour, on every page in the
/// navigation; the dashboard script asks every 10 seconds and shows a message when one finishes. Only the user's own
/// actions, never anybody else's.
/// </summary>
internal sealed class MyRequests(ServerDbContext db, TimeProvider timeProvider)
{
    /// <summary>Path of the JSON the dashboard script polls.</summary>
    public const string Path = "/dashboard/my-requests";

    /// <summary>Finished requests stay listed this long.</summary>
    public static readonly TimeSpan FinishedVisible = TimeSpan.FromHours(1);

    /// <summary>At most this many requests are listed.</summary>
    public const int MaxItems = 20;

    public async Task<IReadOnlyList<MyRequest>> ListAsync(string user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        var since = timeProvider.GetUtcNow().UtcDateTime - FinishedVisible;
        var rows = await db.AgentActions.AsNoTracking()
            .Where(a => a.RequestedBy == user && (a.State == AgentActionState.Queued || a.State == AgentActionState.Delivered || (a.CompletedAtUtc != null && a.CompletedAtUtc >= since)))
            .OrderByDescending(a => a.RequestedAtUtc)
            .Take(MaxItems)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { Action = a, c.HostName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r =>
        {
            var a = r.Action;
            var open = a.State is AgentActionState.Queued or AgentActionState.Delivered;
            var step = a.State switch
            {
                AgentActionState.Queued => "requested, waits for the next heartbeat",
                AgentActionState.Delivered => $"picked up {Components.DisplayTime.Format(a.DeliveredAtUtc ?? a.RequestedAtUtc)}, running",
                _ => $"done {Components.DisplayTime.Format(a.CompletedAtUtc ?? a.RequestedAtUtc)}",
            };
            return new MyRequest(
                a.Id,
                r.HostName,
                $"/servers/{a.ClientId:D}?tab=activity",
                AgentActionText.Label(a.Kind, a.Scope),
                a.State.ToString(),
                AgentActionText.StateClass(a.State),
                open,
                step,
                Components.DisplayTime.Format(a.RequestedAtUtc),
                open ? null : a.Detail,
                a.LogFileSize is not null && a.State == AgentActionState.Succeeded ? AgentLogEndpoints.DownloadPath(a.Id) : null);
        })];
    }

}

/// <summary>The JSON of "My requests" for the dashboard script (signed-in users, their own actions only).</summary>
internal static class MyRequestsEndpoints
{
    public static IEndpointRouteBuilder MapMyRequestsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(MyRequests.Path, async (HttpContext context, MyRequests requests, CancellationToken cancellationToken) =>
            context.User.Identity?.Name is { Length: > 0 } user
                ? Results.Json(await requests.ListAsync(user, cancellationToken).ConfigureAwait(false))
                : Results.Unauthorized())
            .RequireAuthorization(AdminRoles.Policies.Read);
        return endpoints;
    }
}
