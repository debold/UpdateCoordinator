using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>Kind of an entry in the activity list of a server.</summary>
internal enum ClientActivityKind
{
    Registered = 0,
    Approved = 1,
    Rejected = 2,
    Inventory = 3,
    Search = 4,
    Installation = 5,
    RemoteAction = 6,
    Restarted = 7,
    AgentUpdated = 8,
    AgentSettings = 9,
}

/// <summary>An entry in the activity list of a server: what the agent did or reported, without heartbeats.</summary>
internal sealed class ClientActivityEntity
{
    public long Id { get; set; }

    public Guid ClientId { get; set; }

    public DateTime AtUtc { get; set; }

    public ClientActivityKind Kind { get; set; }

    /// <summary>Plain-language summary written by the central server, never raw agent text.</summary>
    public required string Detail { get; set; }
}

/// <summary>
/// Activity list of a server (docs/06-dashboard.md, server detail): registration, approval, inventory, update searches,
/// installations, remote actions, restarts and agent updates. Entries are added to the caller's unit of work, so they are
/// saved together with the change they describe; entries older than <see cref="Retention"/> are deleted periodically.
/// </summary>
internal sealed class ClientActivityLog(ServerDbContext db, TimeProvider timeProvider)
{
    public const int MaxDetailLength = 256;

    /// <summary>How long entries are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Adds an entry; it is saved with the caller's next SaveChanges.</summary>
    public void Add(Guid clientId, ClientActivityKind kind, string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        db.ClientActivities.Add(new ClientActivityEntity
        {
            ClientId = clientId,
            AtUtc = timeProvider.GetUtcNow().UtcDateTime,
            Kind = kind,
            Detail = detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength],
        });
    }

    /// <summary>Adds an entry and saves it at once (for callers that update with ExecuteUpdate).</summary>
    public async Task AddNowAsync(Guid clientId, ClientActivityKind kind, string detail, CancellationToken cancellationToken)
    {
        Add(clientId, kind, detail);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteOldAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - Retention;
        return await db.ClientActivities.Where(a => a.AtUtc < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string Label(ClientActivityKind kind) => kind switch
    {
        ClientActivityKind.Registered => "Registered",
        ClientActivityKind.Approved => "Approved",
        ClientActivityKind.Rejected => "Rejected",
        ClientActivityKind.Inventory => "Inventory",
        ClientActivityKind.Search => "Update search",
        ClientActivityKind.Installation => "Installation",
        ClientActivityKind.RemoteAction => "Remote action",
        ClientActivityKind.Restarted => "Restart",
        ClientActivityKind.AgentUpdated => "Agent update",
        ClientActivityKind.AgentSettings => "Agent settings",
        _ => kind.ToString(),
    };
}
