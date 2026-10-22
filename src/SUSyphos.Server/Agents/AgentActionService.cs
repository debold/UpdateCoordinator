using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

internal enum LogUploadResult
{
    Stored,
    NotFound,
    Invalid,
}

/// <summary>
/// Remote action queue (docs/03-communication-protocol.md, section 6): administrators queue actions, the heartbeat
/// announces them, the agent fetches and confirms them. The central server never contacts the agent.
/// </summary>
internal sealed partial class AgentActionService(
    ServerDbContext db, AuditLog audit, Microsoft.Extensions.Options.IOptions<AdministrationOptions> administration, TimeProvider timeProvider, ILogger<AgentActionService> logger)
{
    /// <summary>Upper bound of open actions per server, so a stuck agent cannot pile up work.</summary>
    public const int MaxOpenPerClient = 10;

    /// <summary>A queued action the agent never fetched expires after this time.</summary>
    public static readonly TimeSpan QueuedLifetime = TimeSpan.FromHours(24);

    /// <summary>A fetched action without result expires after this time (agent restarted or crashed).</summary>
    public static readonly TimeSpan DeliveredLifetime = TimeSpan.FromHours(1);

    public Task<Outcome> QueueAsync(Guid clientId, AgentActionKind kind, string actor, CancellationToken cancellationToken) =>
        kind is AgentActionKind.Uninstall or AgentActionKind.UninstallRestartInWindow
            ? Task.FromResult(new Outcome("Choose the update to uninstall."))
            : QueueCoreAsync(clientId, kind, actor, null, cancellationToken);

    /// <summary>
    /// Queues the uninstallation of an update the server has installed (per its inventory), with the restart right after
    /// or in the next maintenance window. The KB number goes along so the agent can use DISM where Windows Update cannot.
    /// </summary>
    public async Task<Outcome> QueueUninstallAsync(Guid clientId, Guid updateId, bool restartNow, string actor, CancellationToken cancellationToken)
    {
        var installed = await db.ClientUpdates.AsNoTracking()
            .Where(u => u.ClientId == clientId && u.UpdateId == updateId)
            .OrderByDescending(u => u.Revision)
            .Select(u => new { u.UpdateId, u.Revision })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (installed is null)
        {
            return new Outcome("The server does not report this update as installed.");
        }

        var update = await db.Updates.AsNoTracking().Where(u => u.UpdateId == updateId && u.Revision == installed.Revision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var kind = restartNow ? AgentActionKind.Uninstall : AgentActionKind.UninstallRestartInWindow;
        return await QueueCoreAsync(clientId, kind, actor, (updateId, installed.Revision, update?.KbArticle), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues the uninstallation on every approved server that reports the update as installed; returns how many got it.</summary>
    public async Task<int> QueueUninstallEverywhereAsync(Guid updateId, bool restartNow, string actor, CancellationToken cancellationToken)
    {
        var approved = db.Clients.Where(c => c.State == RegistrationState.Approved).Select(c => c.Id);
        var clients = await db.ClientUpdates.AsNoTracking()
            .Where(u => u.UpdateId == updateId && approved.Contains(u.ClientId))
            .Select(u => u.ClientId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var queued = 0;
        foreach (var clientId in clients)
        {
            if ((await QueueUninstallAsync(clientId, updateId, restartNow, actor, cancellationToken).ConfigureAwait(false)).Succeeded)
            {
                queued++;
            }
        }

        return queued;
    }

    private async Task<Outcome> QueueCoreAsync(Guid clientId, AgentActionKind kind, string actor, (Guid UpdateId, int Revision, string? KbArticle)? update, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind))
        {
            return new Outcome("The action is unknown.");
        }

        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server no longer exists.");
        }

        if (client.State != RegistrationState.Approved)
        {
            return new Outcome("Actions can only be queued for approved servers.");
        }

        var open = await db.AgentActions.CountAsync(a => a.ClientId == clientId && (a.State == AgentActionState.Queued || a.State == AgentActionState.Delivered), cancellationToken).ConfigureAwait(false);
        if (open >= MaxOpenPerClient)
        {
            return new Outcome($"At most {MaxOpenPerClient} actions can wait per server.");
        }

        db.AgentActions.Add(new AgentActionEntity
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Kind = kind,
            State = AgentActionState.Queued,
            RequestedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            RequestedBy = actor,
            UpdateId = update?.UpdateId,
            Revision = update?.Revision,
            KbArticle = update?.KbArticle,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = update is { } named ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{kind} {named.UpdateId:D} revision {named.Revision}{(named.KbArticle is null ? null : $" (KB{named.KbArticle})")}") : kind.ToString();
        await audit.WriteAsync(actor, "Action.Queued", $"{client.HostName} ({clientId:D})", details, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> CancelAsync(Guid clientId, Guid actionId, string actor, CancellationToken cancellationToken)
    {
        // Conditional update: an action the agent already fetched cannot be cancelled any more.
        var cancelled = await db.AgentActions
            .Where(a => a.Id == actionId && a.ClientId == clientId && a.State == AgentActionState.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentActionState.Cancelled).SetProperty(a => a.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime), cancellationToken)
            .ConfigureAwait(false);
        if (cancelled == 0)
        {
            return new Outcome("Only actions the agent has not fetched yet can be cancelled.");
        }

        await audit.WriteAsync(actor, "Action.Cancelled", clientId.ToString("D"), actionId.ToString("D"), cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public Task<int> CountQueuedAsync(Guid clientId, CancellationToken cancellationToken) =>
        db.AgentActions.CountAsync(a => a.ClientId == clientId && a.State == AgentActionState.Queued, cancellationToken);

    /// <summary>Hands out the queued actions of a client, oldest first, and marks them as delivered.</summary>
    public async Task<AgentActionList> FetchAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var actions = await db.AgentActions
            .Where(a => a.ClientId == clientId && a.State == AgentActionState.Queued)
            .OrderBy(a => a.RequestedAtUtc)
            .Take(AgentActionList.MaxActions)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var action in actions)
        {
            action.State = AgentActionState.Delivered;
            action.DeliveredAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new AgentActionList([.. actions.Select(a => new AgentAction(a.Id, a.Kind, new DateTimeOffset(DateTime.SpecifyKind(a.RequestedAtUtc, DateTimeKind.Utc)), a.UpdateId, a.Revision, a.KbArticle))]);
    }

    /// <summary>Records the result; false if the action is unknown, belongs to another client or was not delivered.</summary>
    public async Task<bool> CompleteAsync(Guid clientId, Guid actionId, AgentActionResult result, CancellationToken cancellationToken)
    {
        var action = await db.AgentActions
            .SingleOrDefaultAsync(a => a.Id == actionId && a.ClientId == clientId && a.State == AgentActionState.Delivered, cancellationToken)
            .ConfigureAwait(false);
        if (action is null)
        {
            return false;
        }

        action.State = result.Status switch
        {
            AgentActionStatus.Succeeded => AgentActionState.Succeeded,
            AgentActionStatus.Rejected => AgentActionState.Rejected,
            _ => AgentActionState.Failed,
        };
        action.CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        // The detail comes from the agent: control characters are dropped before it reaches the dashboard and logs.
        action.Detail = result.Detail is null ? null : new string([.. result.Detail.Where(c => !char.IsControl(c))]);
        db.ClientActivities.Add(new ClientActivityEntity
        {
            ClientId = clientId,
            AtUtc = action.CompletedAtUtc.Value,
            Kind = ClientActivityKind.RemoteAction,
            Detail = $"{action.Kind}: {action.State}{(action.Detail is { Length: > 0 } detail ? $" ({(detail.Length > 160 ? detail[..160] : detail)})" : string.Empty)}",
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogCompleted(clientId, action.Kind, action.State);
        return true;
    }

    /// <summary>
    /// Stores the log archive of a delivered "UploadLogs" action of this client. The file name is derived from the action
    /// ID only; the archive is checked to be a readable ZIP file but never extracted on the central server.
    /// </summary>
    public async Task<LogUploadResult> StoreLogsAsync(Guid clientId, Guid actionId, byte[] archive, CancellationToken cancellationToken)
    {
        var action = await db.AgentActions
            .SingleOrDefaultAsync(a => a.Id == actionId && a.ClientId == clientId && a.State == AgentActionState.Delivered && a.Kind == AgentActionKind.UploadLogs, cancellationToken)
            .ConfigureAwait(false);
        if (action is null)
        {
            return LogUploadResult.NotFound;
        }

        if (!IsZip(archive))
        {
            return LogUploadResult.Invalid;
        }

        RestrictedFile.Write(LogPath(actionId), archive);
        action.LogFileSize = archive.Length;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return LogUploadResult.Stored;
    }

    /// <summary>Path of the stored archive of an action; the name contains only the action ID.</summary>
    public string LogPath(Guid actionId) => Path.Combine(administration.Value.AgentLogDirectory, $"{actionId:N}.zip");

    /// <summary>Deletes log archives older than the retention time.</summary>
    public async Task<int> DeleteOldLogsAsync(CancellationToken cancellationToken)
    {
        var before = timeProvider.GetUtcNow().UtcDateTime.AddDays(-administration.Value.AgentLogRetentionDays);
        var old = await db.AgentActions.Where(a => a.LogFileSize != null && a.RequestedAtUtc < before).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var action in old)
        {
            File.Delete(LogPath(action.Id));
            action.LogFileSize = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return old.Count;
    }

    private static bool IsZip(byte[] archive)
    {
        if (archive.Length < 22 || archive.Length > AgentActionList.MaxLogBytes)
        {
            return false;
        }

        try
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(archive, writable: false), System.IO.Compression.ZipArchiveMode.Read);
            return zip.Entries.Count <= 1_000;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Expires actions that were never fetched or never confirmed.</summary>
    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var queuedBefore = now - QueuedLifetime;
        var deliveredBefore = now - DeliveredLifetime;
        return await db.AgentActions
            .Where(a => (a.State == AgentActionState.Queued && a.RequestedAtUtc < queuedBefore) || (a.State == AgentActionState.Delivered && a.DeliveredAtUtc < deliveredBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentActionState.Expired).SetProperty(a => a.CompletedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {ClientId}: action {Kind} finished with {State}.")]
    private partial void LogCompleted(Guid clientId, AgentActionKind kind, AgentActionState state);
}
