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
/// <summary>A server that has an update installed, with what an uninstallation can be aimed at: its ring and groups.</summary>
internal sealed record UninstallTarget(Guid ClientId, string HostName, int? RingId, IReadOnlyList<int> GroupIds);

/// <summary>Where an uninstallation goes: the servers of these rings, groups or the named servers; nothing named means every server that has the update.</summary>
internal sealed record UninstallScope(IReadOnlyCollection<int>? Rings = null, IReadOnlyCollection<int>? Groups = null, IReadOnlyCollection<Guid>? Clients = null)
{
    public static readonly UninstallScope All = new();

    public bool Contains(UninstallTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return (Rings, Groups, Clients) is (null or { Count: 0 }, null or { Count: 0 }, null or { Count: 0 })
            || (target.RingId is { } ring && Rings?.Contains(ring) == true)
            || target.GroupIds.Any(g => Groups?.Contains(g) == true)
            || Clients?.Contains(target.ClientId) == true;
    }
}

internal sealed partial class AgentActionService(
    ServerDbContext db, AuditLog audit, Microsoft.Extensions.Options.IOptions<AdministrationOptions> administration, TimeProvider timeProvider, ILogger<AgentActionService> logger)
{
    /// <summary>Upper bound of open actions per server, so a stuck agent cannot pile up work.</summary>
    public const int MaxOpenPerClient = 10;

    /// <summary>A queued action the agent never fetched expires after this time.</summary>
    public static readonly TimeSpan QueuedLifetime = TimeSpan.FromHours(24);

    /// <summary>A fetched action without result expires after this time (agent restarted or crashed).</summary>
    public static readonly TimeSpan DeliveredLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// A fetched uninstallation may wait for its maintenance window or the lease of a coordinated group before the agent
    /// runs it and reports the result, so it expires only after a week.
    /// </summary>
    public static readonly TimeSpan HeldLifetime = TimeSpan.FromDays(7);

    /// <summary>The kind of an uninstallation from the choice in the dialogs ("now", "window", "in-window"); null for anything else.</summary>
    public static AgentActionKind? UninstallKind(string? timing) => timing switch
    {
        "now" => AgentActionKind.Uninstall,
        "window" => AgentActionKind.UninstallRestartInWindow,
        "in-window" => AgentActionKind.UninstallInWindow,
        _ => null,
    };

    /// <summary>The actions the agent holds until they may run (uninstallations).</summary>
    public static bool IsHeld(AgentActionKind kind) => kind is AgentActionKind.Uninstall or AgentActionKind.UninstallRestartInWindow or AgentActionKind.UninstallInWindow;

    public Task<Outcome> QueueAsync(Guid clientId, AgentActionKind kind, string actor, CancellationToken cancellationToken) =>
        IsHeld(kind) ? Task.FromResult(new Outcome("Choose the update to uninstall."))
            : kind == AgentActionKind.UninstallAgent ? Task.FromResult(new Outcome("The agent is uninstalled by removing the server."))
            : QueueCoreAsync(clientId, kind, actor, null, cancellationToken);

    /// <summary>Queues the uninstallation of the agent with the given action ID; only <see cref="SUSyphos.Server.Approval.ServerRemoval"/> uses it.</summary>
    internal Task<Outcome> QueueUninstallAgentAsync(Guid clientId, Guid actionId, string actor, CancellationToken cancellationToken) =>
        QueueCoreAsync(clientId, AgentActionKind.UninstallAgent, actor, null, cancellationToken, actionId);

    /// <summary>
    /// Queues the uninstallation of an update the server has installed (per its inventory), with the restart right after
    /// or in the next maintenance window. The KB number goes along so the agent can use DISM where Windows Update cannot.
    /// </summary>
    public async Task<Outcome> QueueUninstallAsync(Guid clientId, Guid updateId, AgentActionKind kind, string actor, CancellationToken cancellationToken)
    {
        if (!IsHeld(kind))
        {
            return new Outcome("Choose when the server uninstalls the update and restarts.");
        }

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
        if (update is not null && !SUSyphos.Server.Rollouts.UninstallCandidates.IsRemovable(update.Classification, update.Title))
        {
            return new Outcome("Windows cannot uninstall this update this way (definition updates, the Defender platform, the removal tool and cumulative updates for .NET Framework).");
        }

        return await QueueCoreAsync(clientId, kind, actor, (updateId, installed.Revision, update?.KbArticle), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The approved servers that report the update as installed, with ring and groups (the targets of an uninstallation).</summary>
    public async Task<List<UninstallTarget>> UninstallTargetsAsync(Guid updateId, CancellationToken cancellationToken)
    {
        var approved = db.Clients.Where(c => c.State == RegistrationState.Approved);
        var clients = await db.ClientUpdates.AsNoTracking()
            .Where(u => u.UpdateId == updateId)
            .Join(approved, u => u.ClientId, c => c.Id, (u, c) => new { c.Id, c.HostName, c.RingId })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = clients.Select(c => c.Id).ToList();
        var groups = await db.ClientGroups.AsNoTracking().Where(g => ids.Contains(g.ClientId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. clients.Select(c => new UninstallTarget(c.Id, c.HostName, c.RingId, [.. groups.Where(g => g.ClientId == c.Id).Select(g => g.GroupId)])).OrderBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Queues the uninstallation on the servers of the scope that report the update as installed (all of them when the
    /// scope names nothing); returns how many got it. In coordinated groups the agents take their turns through the lease.
    /// </summary>
    public async Task<int> QueueUninstallForAsync(Guid updateId, AgentActionKind kind, UninstallScope scope, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var queued = 0;
        foreach (var target in (await UninstallTargetsAsync(updateId, cancellationToken).ConfigureAwait(false)).Where(scope.Contains))
        {
            if ((await QueueUninstallAsync(target.ClientId, updateId, kind, actor, cancellationToken).ConfigureAwait(false)).Succeeded)
            {
                queued++;
            }
        }

        return queued;
    }

    private async Task<Outcome> QueueCoreAsync(Guid clientId, AgentActionKind kind, string actor, (Guid UpdateId, int Revision, string? KbArticle)? update, CancellationToken cancellationToken, Guid? actionId = null)
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
            Id = actionId ?? Guid.NewGuid(),
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
        var heldBefore = now - HeldLifetime;
        AgentActionKind[] held = [AgentActionKind.Uninstall, AgentActionKind.UninstallRestartInWindow, AgentActionKind.UninstallInWindow];
        return await db.AgentActions
            .Where(a => (a.State == AgentActionState.Queued && a.RequestedAtUtc < queuedBefore)
                || (a.State == AgentActionState.Delivered && !held.Contains(a.Kind) && a.DeliveredAtUtc < deliveredBefore)
                || (a.State == AgentActionState.Delivered && held.Contains(a.Kind) && a.DeliveredAtUtc < heldBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentActionState.Expired).SetProperty(a => a.CompletedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {ClientId}: action {Kind} finished with {State}.")]
    private partial void LogCompleted(Guid clientId, AgentActionKind kind, AgentActionState state);
}
