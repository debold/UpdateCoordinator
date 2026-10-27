using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>A finished remote action shown on the Approvals page until someone acknowledges it.</summary>
/// <param name="ActionId">The action.</param>
/// <param name="ClientId">The server it ran on.</param>
/// <param name="HostName">Name of the server.</param>
/// <param name="Kind">What was asked.</param>
/// <param name="State">How it ended.</param>
/// <param name="RequestedBy">Who asked for it.</param>
/// <param name="FinishedAtUtc">When it ended.</param>
/// <param name="Detail">The agent's answer, or null.</param>
/// <param name="Failed">Failed, refused or never answered (needs a look); otherwise finished with a result worth knowing.</param>
internal sealed record ActionResult(Guid ActionId, Guid ClientId, string HostName, AgentActionKind Kind, AgentActionState State, string RequestedBy, DateTime FinishedAtUtc, string? Detail, bool Failed);

/// <summary>
/// Results of manual remote actions where operators see them (Marc, 2026-10-27): failed, refused and unanswered actions wait
/// on the Approvals page until someone acknowledges them, and so do finished actions with a result (logs to download, an
/// update installed or uninstalled). Routine actions that only trigger a search or an inventory are left out. The
/// acknowledgements live in the settings table and are forgotten with the results after <see cref="ShownFor"/>.
/// </summary>
internal sealed class ActionResults(ServerDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    private const string AcknowledgedKey = "Actions.Acknowledged";

    /// <summary>How long a finished action is shown.</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromDays(7);

    /// <summary>At most this many results are listed.</summary>
    public const int MaxShown = 200;

    /// <summary>Actions whose success brings a result the requester waits for.</summary>
    private static readonly AgentActionKind[] WithResult =
    [
        AgentActionKind.UploadLogs, AgentActionKind.InstallNow, AgentActionKind.InstallNowRestartInWindow,
        AgentActionKind.Uninstall, AgentActionKind.UninstallRestartInWindow, AgentActionKind.UninstallInWindow,
        AgentActionKind.RestartServer, AgentActionKind.RestartServerInWindow,
    ];

    private static readonly AgentActionState[] FailedStates = [AgentActionState.Failed, AgentActionState.Rejected, AgentActionState.Expired];

    public static bool IsFailure(AgentActionState state) => FailedStates.Contains(state);

    /// <summary>Whether a successful action of this kind brings a result worth telling the requester.</summary>
    public static bool HasResult(AgentActionKind kind) => WithResult.Contains(kind);

    /// <summary>The finished actions nobody acknowledged yet, failures first, newest first.</summary>
    public async Task<List<ActionResult>> OpenAsync(CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime - ShownFor;
        var acknowledged = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Keys.ToHashSet();
        var actions = await (
                from action in db.AgentActions.AsNoTracking()
                join client in db.Clients on action.ClientId equals client.Id
                where action.CompletedAtUtc != null && action.CompletedAtUtc >= since
                    && (FailedStates.Contains(action.State) || (action.State == AgentActionState.Succeeded && WithResult.Contains(action.Kind)))
                    && action.Kind != AgentActionKind.UninstallAgent
                orderby action.CompletedAtUtc descending
                select new { action.Id, action.ClientId, client.HostName, action.Kind, action.State, action.RequestedBy, action.CompletedAtUtc, action.Detail })
            .Take(MaxShown * 2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. actions
            .Where(a => !acknowledged.Contains(a.Id))
            .Select(a => new ActionResult(a.Id, a.ClientId, a.HostName, a.Kind, a.State, a.RequestedBy, a.CompletedAtUtc!.Value, a.Detail, IsFailure(a.State)))
            .OrderByDescending(a => a.Failed)
            .ThenByDescending(a => a.FinishedAtUtc)
            .Take(MaxShown)];
    }

    /// <summary>Failed actions nobody acknowledged: part of the count on the Approvals link.</summary>
    public async Task<int> OpenFailuresAsync(CancellationToken cancellationToken) =>
        (await OpenAsync(cancellationToken).ConfigureAwait(false)).Count(a => a.Failed);

    /// <summary>Acknowledges the given results (or all shown when <paramref name="actionIds"/> is empty).</summary>
    public async Task<Outcome> AcknowledgeAsync(IReadOnlyCollection<Guid> actionIds, string actor, CancellationToken cancellationToken)
    {
        var open = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var chosen = actionIds.Count == 0 ? open : [.. open.Where(a => actionIds.Contains(a.ActionId))];
        if (chosen.Count == 0)
        {
            return new Outcome("Nothing to acknowledge.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var result in chosen)
        {
            stored[result.ActionId] = now;
        }

        // Forget acknowledgements of results no longer shown.
        var keep = stored.Where(s => s.Value >= now - ShownFor - TimeSpan.FromDays(1)).ToDictionary(s => s.Key, s => s.Value);
        var json = JsonSerializer.Serialize(keep);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == AcknowledgedKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = AcknowledgedKey, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Action.Acknowledged", string.Join(", ", chosen.Select(c => c.HostName).Distinct().Take(10)), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{chosen.Count} results"), cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private async Task<Dictionary<Guid, DateTime>> LoadAsync(CancellationToken cancellationToken)
    {
        var json = await db.Settings.AsNoTracking().Where(s => s.Key == AcknowledgedKey).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return json is null ? [] : JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
