using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>What holds an update back on a server: a hold of the server itself (it can be ended here) or of its ring or a group.</summary>
/// <param name="HoldId">The hold, for ending it.</param>
/// <param name="OwnServer">The hold is for this server.</param>
/// <param name="Target">"this server", "ring 2 Production" or "group SQL".</param>
internal sealed record SelectionHold(int HoldId, bool OwnServer, string Target);

/// <summary>A missing update of a server and which actions of a selection apply to it; a blocker is null where the action applies.</summary>
internal sealed record MissingSelectionRow(DeploymentEntry Entry, string Title, bool Software, SelectionHold? Hold, string? InstallBlocker, string? HoldBlocker, string? EndHoldBlocker)
{
    public Guid UpdateId => Entry.UpdateId;

    /// <summary>Released for the ring of the server (or for the server alone); false: installing it releases it for this server first.</summary>
    public bool Released => Entry.EarliestInstallAtUtc is not null;
}

/// <summary>
/// An installed update or WinGet program of a server and which actions of a selection apply to it. The key is
/// "u:{update id}" for a Windows update, "p:{package id}" for a program.
/// </summary>
internal sealed record InstalledSelectionRow(string Key, string Title, bool Software, DateTime? InstalledAtUtc, bool? Uninstallable, Guid? UpdateId, int? Revision, string? PackageId, SelectionHold? Hold, string? UninstallBlocker, string? HoldBlocker);

/// <summary>The result of an action on a selection: how many it was applied to, and which were skipped and why.</summary>
internal sealed record SelectionResult(int Done, IReadOnlyList<(string Title, string Reason)> Skipped, string? Error = null)
{
    public static SelectionResult Failed(string error) => new(0, [], error);
}

/// <summary>
/// Actions on a selection of the updates of one server (Marc, 2026-11-16, concept
/// https://claude.ai/artifact/DoHHWfdw78hgqK1zmv1Bc2): install now (an update still waiting for the ring is released for this
/// server alone), hold and end a hold for the server, and uninstall. Each row says which actions apply and why not, so the
/// page and the actions agree; rows an action does not apply to are skipped and named.
/// </summary>
internal sealed class ServerUpdateSelection(ServerDbContext db, DeploymentStatus deployment, AgentActionService actions, UpdateHolds holds, ReleasePlanner releases, AuditLog audit, TimeProvider timeProvider)
{
    /// <summary>Most rows one selection may hold.</summary>
    public const int MaxSelection = AgentAction.MaxUpdateIds;

    /// <summary>The missing updates of the server: reported by its last search, or released and not installed yet.</summary>
    public async Task<List<MissingSelectionRow>> MissingAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return [];
        }

        var entries = await deployment.GetAsync(null, clientId, null, cancellationToken).ConfigureAwait(false);
        var available = (await db.ClientAvailableUpdates.AsNoTracking().Where(a => a.ClientId == clientId).Select(a => new { a.UpdateId, a.Revision }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(a => (a.UpdateId, a.Revision)).ToHashSet();
        var open = entries.Where(e => available.Contains((e.UpdateId, e.Revision)) || (e.EarliestInstallAtUtc is not null && e.State != DeploymentState.Successful)).ToList();
        var ids = open.Select(e => e.UpdateId).Distinct().ToList();
        var updates = await db.Updates.AsNoTracking().Where(u => ids.Contains(u.UpdateId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var notOffered = (await db.PackageVersions.AsNoTracking().Where(v => v.CachedAtUtc == null && ids.Contains(v.UpdateId)).Select(v => v.UpdateId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var softwareEnabled = await Software.SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false);
        var heldBy = await HoldsOfAsync(client, ids, cancellationToken).ConfigureAwait(false);

        return [.. open
            .OrderByDescending(e => e.State).ThenBy(e => e.EarliestInstallAtUtc)
            .Select(e =>
            {
                var update = updates.FirstOrDefault(u => u.UpdateId == e.UpdateId && u.Revision == e.Revision);
                var software = update?.Kind == UpdateKind.Software;
                var hold = heldBy.GetValueOrDefault((e.UpdateId, e.Revision));
                var install = e.RestartPending ? "Installed; it waits for the restart that completes it."
                    : hold is not null ? $"On hold for {hold.Target}; end the hold first."
                    : software && !softwareEnabled ? "Software management is switched off."
                    : software && client.SoftwareState != SoftwareSupportState.Supported ? "WinGet does not work on this server."
                    : software && notOffered.Contains(e.UpdateId) ? "Not in the WinGet source of the central server yet (download or virus scan pending)."
                    : null;
                var holdBlocker = hold is { OwnServer: true } ? "Already on hold for this server." : null;
                var endHold = hold is null ? "Not on hold." : hold.OwnServer ? null : $"On hold for {hold.Target}; end that hold on the update page.";
                return new MissingSelectionRow(e, UpdateText.Title(update, e.UpdateId), software, hold, install, holdBlocker, endHold);
            })];
    }

    /// <summary>The installed updates the server can uninstall and its programs WinGet knows, newest installation first.</summary>
    public async Task<List<InstalledSelectionRow>> InstalledAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return [];
        }

        var candidates = await UninstallCandidates.LoadAsync(db, clientId, cancellationToken).ConfigureAwait(false);
        var revisions = (await db.ClientUpdates.AsNoTracking().Where(u => u.ClientId == clientId).Select(u => new { u.UpdateId, u.Revision }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.UpdateId).ToDictionary(g => g.Key, g => g.Max(u => u.Revision));
        var heldBy = await HoldsOfAsync(client, candidates.Select(c => c.UpdateId).ToList(), cancellationToken).ConfigureAwait(false);
        var waiting = (await db.AgentActions.AsNoTracking()
                .Where(a => a.ClientId == clientId && (a.State == AgentActionState.Queued || a.State == AgentActionState.Delivered)
                    && (a.Kind == AgentActionKind.Uninstall || a.Kind == AgentActionKind.UninstallRestartInWindow || a.Kind == AgentActionKind.UninstallInWindow))
                .Select(a => new { a.UpdateId, a.PackageId })
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        var waitingUpdates = waiting.Where(w => w.PackageId == null && w.UpdateId != null).Select(w => w.UpdateId!.Value).ToHashSet();
        var waitingPackages = waiting.Where(w => w.PackageId != null).Select(w => w.PackageId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = candidates.Select(c =>
        {
            var revision = revisions.GetValueOrDefault(c.UpdateId);
            var hold = heldBy.GetValueOrDefault((c.UpdateId, revision));
            return new InstalledSelectionRow($"u:{c.UpdateId:D}", c.Title, false, c.InstalledAtUtc, c.Uninstallable, c.UpdateId, revision, null, hold,
                waitingUpdates.Contains(c.UpdateId) ? "Its uninstallation already waits on the server." : null,
                hold is { OwnServer: true } ? "Already on hold for this server." : null);
        }).ToList();

        var softwareEnabled = await Software.SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false);
        var programs = await db.ClientSoftware.AsNoTracking().Where(s => s.ClientId == clientId && s.PackageId != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.AddRange(programs
            .DistinctBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(p => new InstalledSelectionRow($"p:{p.PackageId}", $"{p.DisplayName} {p.InstalledVersion ?? p.DisplayVersion}".TrimEnd(), true, null, true, null, null, p.PackageId, null,
                SoftwareUninstallBlocker(p.DisplayName, p.PackageId!, softwareEnabled, client.SoftwareState, waitingPackages),
                "Software is kept away by ending its assignment on the package page.")));
        return rows;
    }

    /// <summary>Why a program cannot be uninstalled with WinGet from the dashboard; null when it can (Marc, 2026-11-14).</summary>
    public static string? SoftwareUninstallBlocker(string displayName, string? packageId, bool softwareEnabled, SoftwareSupportState? state, IReadOnlySet<string> waiting)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(waiting);
        return packageId is null ? "Not known to WinGet. Remove it on the server."
            : displayName.StartsWith("SUSyphos", StringComparison.OrdinalIgnoreCase) ? "SUSyphos itself is removed with Remove server."
            : !softwareEnabled ? "Software management is switched off."
            : state != SoftwareSupportState.Supported ? "WinGet does not work on this server. Remove it on the server."
            : waiting.Contains(packageId) ? "Its uninstallation already waits on the server."
            : null;
    }

    /// <summary>
    /// Installs the selected missing updates now. An update still waiting for the ring is released for this server alone
    /// first (audited); held, restart-pending and not yet offered software rows are skipped.
    /// </summary>
    public async Task<SelectionResult> InstallAsync(Guid clientId, IReadOnlyCollection<Guid> selected, AgentActionKind kind, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (Validate(selected) is { } error)
        {
            return SelectionResult.Failed(error);
        }

        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null || client.State != RegistrationState.Approved)
        {
            return SelectionResult.Failed("Updates can only be installed on approved servers.");
        }

        if (!AgentActionService.SupportsSelection(client.AgentVersion))
        {
            return SelectionResult.Failed($"The agent of this server installs a selection from version {AgentAction.SelectionSinceVersion} on; update the agent or use \"Install all released now\".");
        }

        var (rows, skipped) = Pick(await MissingAsync(clientId, cancellationToken).ConfigureAwait(false), selected, r => r.UpdateId, r => r.Title, r => r.InstallBlocker);
        if (rows.Count == 0)
        {
            return new SelectionResult(0, skipped, "None of the selected updates can be installed now.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var own = rows.Where(r => !r.Released).ToList();
        if (own.Count > 0)
        {
            var existing = (await db.ServerReleases.Where(r => r.ClientId == clientId).Select(r => new { r.UpdateId, r.Revision }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(r => (r.UpdateId, r.Revision)).ToHashSet();
            db.ServerReleases.AddRange(own.Where(r => !existing.Contains((r.UpdateId, r.Entry.Revision))).Select(r => new ServerReleaseEntity
            {
                ClientId = clientId,
                UpdateId = r.UpdateId,
                Revision = r.Entry.Revision,
                CreatedBy = actor.Length > 256 ? actor[..256] : actor,
                CreatedAtUtc = now,
            }));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in own)
            {
                await audit.WriteAsync(actor, "Update.ReleasedForServer", $"{client.HostName} ({clientId:D})", $"{row.Title} ({row.UpdateId:D}/{row.Entry.Revision})", cancellationToken).ConfigureAwait(false);
            }

            await releases.SyncAsync(clientId, cancellationToken).ConfigureAwait(false);
        }

        var outcome = await actions.QueueInstallSelectionAsync(clientId, kind, [.. rows.Select(r => (r.UpdateId, r.Entry.Revision))], actor, cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? new SelectionResult(rows.Count, skipped) : new SelectionResult(0, skipped, outcome.Error);
    }

    /// <summary>Holds the selected updates (missing or installed Windows updates) for this server, optionally until a date.</summary>
    public async Task<SelectionResult> HoldAsync(Guid clientId, IReadOnlyCollection<Guid> missing, IReadOnlyCollection<string> installed, string? reason, DateTime? untilUtc, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(installed);
        if (missing.Count + installed.Count == 0 || missing.Count + installed.Count > MaxSelection)
        {
            return SelectionResult.Failed($"Select between 1 and {MaxSelection} updates.");
        }

        // Reason and end date are the same for every row: checked once, as the hold itself checks them.
        if (reason is { } text && (text.Trim().Length > UpdateHoldEntity.MaxReasonLength || text.Any(char.IsControl)))
        {
            return SelectionResult.Failed($"The reason may have at most {UpdateHoldEntity.MaxReasonLength} characters on one line.");
        }

        if (untilUtc is { } until && until <= timeProvider.GetUtcNow().UtcDateTime)
        {
            return SelectionResult.Failed("The end of the hold must lie in the future.");
        }

        var (missingRows, skipped) = Pick(await MissingAsync(clientId, cancellationToken).ConfigureAwait(false), missing, r => r.UpdateId, r => r.Title, r => r.HoldBlocker);
        var (installedRows, skippedInstalled) = Pick(await InstalledAsync(clientId, cancellationToken).ConfigureAwait(false), installed, r => r.Key, r => r.Title, r => r.HoldBlocker);
        var targets = missingRows.Select(r => (r.UpdateId, r.Entry.Revision, r.Title))
            .Concat(installedRows.Select(r => (r.UpdateId!.Value, r.Revision!.Value, r.Title)))
            .DistinctBy(t => (t.Item1, t.Item2))
            .ToList();
        var skippedAll = skipped.Concat(skippedInstalled).ToList();
        var done = 0;
        foreach (var (updateId, revision, title) in targets)
        {
            var outcome = await holds.HoldAsync(updateId, revision, HoldTarget.Server, clientId.ToString("D"), reason, untilUtc, actor, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                done++;
            }
            else
            {
                skippedAll.Add((title, outcome.Error!));
            }
        }

        return new SelectionResult(done, skippedAll, done == 0 ? "None of the selected updates can be held." : null);
    }

    /// <summary>Ends the holds of this server for the selected missing updates; holds of the ring or a group stay.</summary>
    public async Task<SelectionResult> EndHoldAsync(Guid clientId, IReadOnlyCollection<Guid> selected, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (Validate(selected) is { } error)
        {
            return SelectionResult.Failed(error);
        }

        var (rows, skipped) = Pick(await MissingAsync(clientId, cancellationToken).ConfigureAwait(false), selected, r => r.UpdateId, r => r.Title, r => r.EndHoldBlocker);
        var done = 0;
        var skippedAll = skipped.ToList();
        foreach (var row in rows)
        {
            var outcome = await holds.ResumeAsync(row.UpdateId, row.Entry.Revision, row.Hold!.HoldId, actor, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                done++;
            }
            else
            {
                skippedAll.Add((row.Title, outcome.Error!));
            }
        }

        return new SelectionResult(done, skippedAll, done == 0 ? "None of the selected updates is on hold for this server." : null);
    }

    /// <summary>
    /// Uninstalls the selected installed updates and programs, one remote action each (grouped by one batch id); an update
    /// can also be held for this server so it is not installed again.
    /// </summary>
    public async Task<SelectionResult> UninstallAsync(Guid clientId, IReadOnlyCollection<string> selected, AgentActionKind kind, bool holdUpdates, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count == 0 || selected.Count > MaxSelection)
        {
            return SelectionResult.Failed($"Select between 1 and {MaxSelection} updates or programs.");
        }

        if (!AgentActionService.IsHeld(kind))
        {
            return SelectionResult.Failed("Choose when the server uninstalls and restarts.");
        }

        var (rows, skipped) = Pick(await InstalledAsync(clientId, cancellationToken).ConfigureAwait(false), selected, r => r.Key, r => r.Title, r => r.UninstallBlocker);
        var batch = Guid.NewGuid();
        var done = 0;
        var skippedAll = skipped.ToList();
        foreach (var row in rows)
        {
            var outcome = row.PackageId is { } package
                ? await actions.QueueSoftwareUninstallAsync(clientId, package, kind, actor, cancellationToken, batch).ConfigureAwait(false)
                : await actions.QueueUninstallAsync(clientId, row.UpdateId!.Value, kind, actor, cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                skippedAll.Add((row.Title, outcome.Error!));
                continue;
            }

            done++;
            if (holdUpdates && row.PackageId is null && row.Hold is null)
            {
                var held = await holds.HoldAsync(row.UpdateId!.Value, row.Revision!.Value, HoldTarget.Server, clientId.ToString("D"), "Uninstalled from the server page", null, actor, cancellationToken).ConfigureAwait(false);
                if (!held.Succeeded)
                {
                    skippedAll.Add((row.Title, $"Uninstalled, but not held: {held.Error}"));
                }
            }
        }

        return new SelectionResult(done, skippedAll, done == 0 ? "None of the selected updates or programs can be uninstalled." : null);
    }

    private static string? Validate(IReadOnlyCollection<Guid> selected) =>
        selected.Count == 0 || selected.Count > MaxSelection ? $"Select between 1 and {MaxSelection} updates." : null;

    /// <summary>The rows the selection names that the action applies to, and the others with their reason.</summary>
    private static (List<TRow> Rows, List<(string Title, string Reason)> Skipped) Pick<TRow, TKey>(IEnumerable<TRow> all, IReadOnlyCollection<TKey> selected, Func<TRow, TKey> key, Func<TRow, string> title, Func<TRow, string?> blocker)
        where TKey : notnull
    {
        var wanted = selected.ToHashSet();
        var named = all.Where(r => wanted.Contains(key(r))).ToList();
        var rows = named.Where(r => blocker(r) is null).ToList();
        var skipped = named.Where(r => blocker(r) is not null).Select(r => (title(r), blocker(r)!)).ToList();
        var gone = wanted.Count - named.Select(key).Distinct().Count();
        if (gone > 0)
        {
            skipped.Add((gone == 1 ? "1 selected entry" : $"{gone} selected entries", "No longer listed for this server."));
        }

        return (rows, skipped);
    }

    /// <summary>The active holds that apply to the server (its own, its ring's, its groups'), per update.</summary>
    private async Task<Dictionary<(Guid, int), SelectionHold>> HoldsOfAsync(ClientEntity client, IReadOnlyCollection<Guid> updateIds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var groups = await db.ClientGroups.AsNoTracking().Where(m => m.ClientId == client.Id).Select(m => m.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var active = (await UpdateHolds.ActiveAsync(db, now, cancellationToken).ConfigureAwait(false))
            .Where(h => updateIds.Contains(h.UpdateId) && (h.ClientId == client.Id || (h.RingId is { } ring && ring == client.RingId) || (h.GroupId is { } group && groups.Contains(group))))
            .ToList();
        if (active.Count == 0)
        {
            return [];
        }

        var rings = await db.Rings.AsNoTracking().ToDictionaryAsync(r => r.Id, r => $"ring {r.Order} {r.Name}", cancellationToken).ConfigureAwait(false);
        var groupNames = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => $"group {g.Name}", cancellationToken).ConfigureAwait(false);
        return active
            .GroupBy(h => (h.UpdateId, h.Revision))
            .ToDictionary(g => g.Key, g =>
            {
                // The server's own hold first: it is the one that can be ended here.
                var hold = g.OrderBy(h => h.ClientId is null).First();
                return new SelectionHold(hold.Id, hold.ClientId is not null,
                    hold.ClientId is not null ? "this server" : hold.RingId is { } ring ? rings.GetValueOrDefault(ring, "its ring") : groupNames.GetValueOrDefault(hold.GroupId!.Value, "a group"));
            });
    }
}
