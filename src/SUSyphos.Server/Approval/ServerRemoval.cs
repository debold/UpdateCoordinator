using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Approval;

/// <summary>A removal that waits for the agent to uninstall itself.</summary>
/// <param name="ClientId">The server to remove.</param>
/// <param name="ActionId">The "uninstall agent" action whose end decides.</param>
/// <param name="KeepHistory">Remove the server and keep its history (otherwise delete it with the history).</param>
/// <param name="Actor">Who asked for the removal; the removal is recorded in their name.</param>
/// <param name="RequestedAtUtc">When the removal was requested.</param>
internal sealed record PendingRemoval(Guid ClientId, Guid ActionId, bool KeepHistory, string Actor, DateTime RequestedAtUtc);

/// <summary>A host removed from SUSyphos: when its agent registers again, no rule approves it automatically.</summary>
internal sealed record RemovedHost(string HostName, string? DomainName, DateTime RemovedAtUtc);

/// <summary>
/// Removes a server from SUSyphos (Marc, 2026-10-27): with or without its history and, by default, with its agent. The
/// agent gets an "uninstall agent" action and the server is removed once the agent confirms it, or when the action expires
/// because the agent does not answer; a refused action leaves the server in place. Every removed host is remembered, so an
/// agent that was offline and registers again later waits for a manual approval instead of being approved by a rule.
/// Both lists live in the settings table.
/// </summary>
internal sealed partial class ServerRemoval(ServerDbContext db, ClientLifecycle lifecycle, AgentActionService actions, AuditLog audit, TimeProvider timeProvider, ILogger<ServerRemoval> logger)
{
    private const string PendingKey = "Removal.Pending";
    private const string HostsKey = "Removal.Hosts";

    /// <summary>Removed hosts remembered; the oldest are forgotten first.</summary>
    public const int MaxRemembered = 2_000;

    public async Task<Outcome> RemoveAsync(Guid clientId, bool keepHistory, bool uninstallAgent, string actor, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId && c.State != RegistrationState.Deleted, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server no longer exists.");
        }

        var pending = await LoadAsync<PendingRemoval>(PendingKey, cancellationToken).ConfigureAwait(false);
        if (pending.Any(p => p.ClientId == clientId))
        {
            return new Outcome("The removal of this server waits for its agent already.");
        }

        if (!uninstallAgent)
        {
            return await RemoveNowAsync(client, keepHistory, actor, cancellationToken).ConfigureAwait(false);
        }

        if (client.State != RegistrationState.Approved)
        {
            return new Outcome("Only an approved server can uninstall its agent; remove it without uninstalling the agent.");
        }

        var actionId = Guid.NewGuid();
        var queued = await actions.QueueUninstallAgentAsync(clientId, actionId, actor, cancellationToken).ConfigureAwait(false);
        if (!queued.Succeeded)
        {
            return queued;
        }

        await SaveAsync(PendingKey, [.. pending, new PendingRemoval(clientId, actionId, keepHistory, actor, timeProvider.GetUtcNow().UtcDateTime)], cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Client.RemovalRequested", $"{client.HostName} ({clientId:D})", keepHistory ? "uninstall agent, keep history" : "uninstall agent, delete history", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<PendingRemoval?> PendingAsync(Guid clientId, CancellationToken cancellationToken) =>
        (await LoadAsync<PendingRemoval>(PendingKey, cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => p.ClientId == clientId);

    /// <summary>
    /// Finishes the removals whose action is over: removed when the agent confirmed the uninstallation or the action
    /// expired (the agent did not answer); kept when the agent refused, failed or the action was cancelled.
    /// </summary>
    public async Task<int> CompleteAsync(CancellationToken cancellationToken)
    {
        var pending = await LoadAsync<PendingRemoval>(PendingKey, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        var ids = pending.Select(p => p.ActionId).ToList();
        var states = await db.AgentActions.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.State, cancellationToken).ConfigureAwait(false);
        var open = new List<PendingRemoval>();
        var done = 0;
        foreach (var removal in pending)
        {
            var state = states.TryGetValue(removal.ActionId, out var known) ? known : (AgentActionState?)null;
            if (state is AgentActionState.Queued or AgentActionState.Delivered)
            {
                open.Add(removal);
                continue;
            }

            var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == removal.ClientId && c.State != RegistrationState.Deleted, cancellationToken).ConfigureAwait(false);
            if (client is null)
            {
                continue;
            }

            if (state is AgentActionState.Succeeded or AgentActionState.Expired)
            {
                await RemoveNowAsync(client, removal.KeepHistory, removal.Actor, cancellationToken).ConfigureAwait(false);
                done++;
            }
            else
            {
                await audit.WriteAsync(AuditLog.SystemActor, "Client.RemovalAborted", $"{client.HostName} ({client.Id:D})", $"the agent uninstallation ended with {state?.ToString() ?? "no action"}; the server stays", cancellationToken).ConfigureAwait(false);
                LogAborted(client.Id, state?.ToString() ?? "missing");
            }
        }

        await SaveAsync(PendingKey, open, cancellationToken).ConfigureAwait(false);
        return done;
    }

    /// <summary>Whether a host with this name (and domain, where both know one) was removed from SUSyphos before.</summary>
    public async Task<bool> WasRemovedAsync(string hostName, string? domainName, CancellationToken cancellationToken) =>
        (await LoadAsync<RemovedHost>(HostsKey, cancellationToken).ConfigureAwait(false)).Any(h =>
            string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase)
            && (h.DomainName is null || domainName is null || string.Equals(h.DomainName, domainName, StringComparison.OrdinalIgnoreCase)));

    private async Task<Outcome> RemoveNowAsync(ClientEntity client, bool keepHistory, string actor, CancellationToken cancellationToken)
    {
        var outcome = keepHistory
            ? await lifecycle.SoftDeleteAsync(client.Id, actor, cancellationToken).ConfigureAwait(false)
            : await lifecycle.HardDeleteAsync(client.Id, actor, cancellationToken).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            var hosts = await LoadAsync<RemovedHost>(HostsKey, cancellationToken).ConfigureAwait(false);
            hosts.RemoveAll(h => string.Equals(h.HostName, client.HostName, StringComparison.OrdinalIgnoreCase) && string.Equals(h.DomainName, client.DomainName, StringComparison.OrdinalIgnoreCase));
            hosts.Add(new RemovedHost(client.HostName, client.DomainName, timeProvider.GetUtcNow().UtcDateTime));
            await SaveAsync(HostsKey, [.. hosts.OrderByDescending(h => h.RemovedAtUtc).Take(MaxRemembered)], cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<List<T>> LoadAsync<T>(string key, CancellationToken cancellationToken)
    {
        var json = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return json is null ? [] : JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveAsync<T>(string key, List<T> values, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(values);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = key, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removal of client {ClientId} aborted: uninstalling the agent ended with {State}.")]
    private partial void LogAborted(Guid clientId, string state);
}
