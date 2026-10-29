using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Trust;

namespace SUSyphos.Server.Notifications;

/// <summary>Kinds of events administrators are notified about.</summary>
internal enum NotificationKind
{
    ServerPendingApproval,
    CloneSuspected,
    RolloutAlarm,
    GateWaiting,
    LeasesHalted,
    CertificateExpiring,
    CertificateChanged,
    AgentVersionAvailable,
    AgentUpdateFailed,
    BackupFailed,
    ServerVersionAvailable,
    UpdateWithdrawn,
    ServerOffline,
    TrustReset,
    InstallationFailed,
    PreFlightFailed,
    RebootPendingLong,
    InstalledOutside,
    AdminChanged,
    RolloutProgress,
    PauseChanged,
    NewUpdatesDaily,
    DailySummary,
    WeeklySummary,
    DefenderSignaturesOutdated,
    SupportEnding,
    RemoteActionFailed,
    RemoteActionFinished,
}

/// <summary>
/// One condition worth a notification. <see cref="Key"/> identifies it: a notification is sent once while the condition
/// lasts and again only after it ended and came back.
/// </summary>
/// <param name="Key">Identifies the condition: sent once while it lasts.</param>
/// <param name="Kind">The event kind.</param>
/// <param name="Text">What happened, one sentence.</param>
/// <param name="Path">Dashboard page relative to its address; null for no link.</param>
/// <param name="Affected">Who or what is affected (server, update, ring), shown as its own line in e-mails; null if the text says it.</param>
/// <param name="Impact">What it means for the operation, shown as its own line; null if obvious.</param>
internal sealed record NotificationEvent(string Key, NotificationKind Kind, string Text, string? Path, string? Affected = null, string? Impact = null);

/// <summary>Derives the current notification conditions from the stored state, so no feature has to raise events itself.</summary>
internal sealed class NotificationEvents(
    ServerDbContext db,
    ITlsCertificateProvider certificates,
    IOptions<CertificateOptions> certificateOptions,
    TimeProvider timeProvider,
    NotificationSettingsStore? settingsStore = null,
    Rollouts.DeploymentStatus? deployment = null,
    Lifecycle.SupportLifecycle? support = null,
    Security.SecurityBaseline? baseline = null)
{
    private static readonly string[] CertificateActions = [CertificateLifecycle.Renewed, CertificateLifecycle.RolledOver, CertificateLifecycle.FallBack];

    public async Task<List<NotificationEvent>> CollectAsync(CancellationToken cancellationToken)
    {
        var events = new List<NotificationEvent>();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.State == RegistrationState.Pending || c.State == RegistrationState.Blocked)
            .Select(c => new { c.Id, c.HostName, c.DomainName, c.State })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var client in clients)
        {
            var name = client.DomainName is { Length: > 0 } domain ? $"{client.HostName}.{domain}" : client.HostName;
            events.Add(client.State == RegistrationState.Pending
                ? new NotificationEvent($"client.pending:{client.Id}", NotificationKind.ServerPendingApproval, $"New server waiting for approval: {name}", "approvals")
                : new NotificationEvent($"client.blocked:{client.Id}", NotificationKind.CloneSuspected, $"Server blocked, suspected clone: {name}", "approvals"));
        }

        var rings = await (
                from state in db.RolloutRings.AsNoTracking()
                where state.State == RingRolloutState.Alarm || state.State == RingRolloutState.AwaitingApproval
                join ring in db.Rings on state.RingId equals ring.Id
                join update in db.Updates on new { state.UpdateId, state.Revision } equals new { update.UpdateId, update.Revision }
                select new { state.UpdateId, state.Revision, state.RingId, state.State, state.Reason, RingName = ring.Name, update.Title, update.KbArticle })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var ring in rings)
        {
            var update = ring.KbArticle is { Length: > 0 } kb ? $"{kb} {ring.Title}" : ring.Title;
            var key = $"{ring.UpdateId}/{ring.Revision}/{ring.RingId}";
            var path = $"updates/{ring.UpdateId}/{ring.Revision}";
            events.Add(ring.State == RingRolloutState.Alarm
                ? new NotificationEvent($"rollout.alarm:{key}", NotificationKind.RolloutAlarm, $"Rollout alarm before ring {ring.RingName}: {update}. {ring.Reason}".TrimEnd(), path)
                : new NotificationEvent($"rollout.gate:{key}", NotificationKind.GateWaiting, $"Gate to ring {ring.RingName} waits for approval: {update}", path));
        }

        // Updates that look withdrawn by Microsoft: reported until the mark goes away.
        var withdrawn = await db.Updates.AsNoTracking()
            .Where(u => u.WithdrawnAtUtc != null)
            .Select(u => new { u.UpdateId, u.Revision, u.KbArticle, u.Title, Stopped = db.Rollouts.Any(r => r.UpdateId == u.UpdateId && r.Revision == u.Revision && r.State == RolloutState.Stopped) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        events.AddRange(withdrawn.Select(u => new NotificationEvent(
            $"update.withdrawn:{u.UpdateId}/{u.Revision}",
            NotificationKind.UpdateWithdrawn,
            $"Update probably withdrawn by Microsoft: {(u.KbArticle is { Length: > 0 } kb ? $"KB{kb} " : string.Empty)}{u.Title}. {(u.Stopped ? "Its rollout is stopped." : "Its rollout was not stopped (notify only); check it now.")}",
            $"updates/{u.UpdateId}/{u.Revision}")));

        var halted = await db.Groups.AsNoTracking().Where(g => g.LeasesHalted).Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        events.AddRange(halted.Select(g => new NotificationEvent($"group.halted:{g.Id}", NotificationKind.LeasesHalted, $"Installations in group {g.Name} stopped after failures; an operator must resume them.", "administration/groups")));

        // A downloaded agent version waits for an administrator; failed self-updates need a look at the server.
        var packages = await db.AgentPackages.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (SUSyphos.Server.AgentPackages.AgentPackageStore.Newest(packages) is { ReleasedThroughRingOrder: null } waiting)
        {
            events.Add(new NotificationEvent($"agent.available:{waiting.Version}", NotificationKind.AgentVersionAvailable, $"Agent version {waiting.Version} is available and waits for release.", "agents"));
        }

        var failedUpdates = await db.Clients.AsNoTracking()
            .Where(c => c.State == RegistrationState.Approved && c.AgentUpdateError != null)
            .Select(c => new { c.Id, c.HostName, c.AgentUpdateError })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        events.AddRange(failedUpdates.Select(c => new NotificationEvent($"agent.update-failed:{c.Id}", NotificationKind.AgentUpdateFailed, $"Agent update failed on {c.HostName}: {c.AgentUpdateError}", $"servers/{c.Id}")));

        // A newer central server release was found; reported once per version.
        var serverUpdate = await db.Settings.AsNoTracking().Where(s => s.Key == AgentPackages.AgentPackageAdministration.ServerUpdateSetting).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (serverUpdate is not null && System.Text.Json.JsonSerializer.Deserialize<AgentPackages.ServerUpdate>(serverUpdate) is { } server)
        {
            events.Add(new NotificationEvent($"server.available:{server.Version}", NotificationKind.ServerVersionAvailable, $"Central server version {server.Version} is available; install it on the central server.", "agents#server"));
        }

        // The last scheduled backup failed: reported until a later run succeeds.
        var backupRun = await db.Settings.AsNoTracking().Where(s => s.Key == Administration.ScheduledBackup.LastRunKey).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (backupRun is not null && System.Text.Json.JsonSerializer.Deserialize<Administration.ScheduledBackupRun>(backupRun) is { Error: { } backupError } failed)
        {
            events.Add(new NotificationEvent($"backup.failed:{failed.AtUtc.Ticks}", NotificationKind.BackupFailed, $"The scheduled backup failed: {backupError}", "administration/backup"));
        }

        var certificate = certificates.Certificate;
        var notAfter = certificate.NotAfter.ToUniversalTime();
        if (!ServerCertificates.IsSelfGenerated(certificate) && notAfter - now <= certificateOptions.Value.RenewBefore)
        {
            events.Add(new NotificationEvent(
                $"certificate.expiring:{certificate.Thumbprint}",
                NotificationKind.CertificateExpiring,
                $"The TLS certificate {certificate.Subject} expires on {notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} and no renewal was found.",
                "administration/certificate"));
        }

        // Automatic certificate changes are events, not lasting states: they are reported for a day after they happened.
        var since = now.AddDays(-1);
        var changes = await db.AuditEntries.AsNoTracking()
            .Where(a => CertificateActions.Contains(a.Action) && a.AtUtc >= since)
            .Select(a => new { a.Id, a.Action, a.Target })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        events.AddRange(changes.Select(c => new NotificationEvent(
            $"audit:{c.Id}",
            NotificationKind.CertificateChanged,
            c.Action == CertificateLifecycle.FallBack
                ? $"The configured TLS certificate was missing; the central server uses its own self-signed certificate ({c.Target})."
                : $"The central server switched to a new TLS certificate ({c.Target}); agents follow automatically.",
            "administration/certificate")));

        var settings = settingsStore is null ? new NotificationSettings() : await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await AddServerEventsAsync(events, settings, now, cancellationToken).ConfigureAwait(false);
        await AddActivityEventsAsync(events, now, cancellationToken).ConfigureAwait(false);
        await AddDigestEventsAsync(events, now, cancellationToken).ConfigureAwait(false);
        return events;
    }

    /// <summary>Audit actions that change who administers SUSyphos.</summary>
    private static readonly string[] AdminActions = ["User.Created", "User.CreatedFromWindows", "User.Deleted", "User.RoleChanged", "WindowsSignIn.GroupMapped", "WindowsSignIn.GroupUnmapped", "WindowsSignIn.Enabled", "WindowsSignIn.Disabled"];

    private static readonly string[] PauseActions = ["Installation.Paused", "Installation.Resumed"];

    /// <summary>States of single servers: offline, failed checks and installations, long pending restarts, installations outside SUSyphos.</summary>
    private async Task AddServerEventsAsync(List<NotificationEvent> events, NotificationSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.State == RegistrationState.Approved)
            .Select(c => new { c.Id, c.HostName, c.LastHeartbeatAtUtc, c.RebootPending, c.LastBootAtUtc, c.HealthChecks, c.AutomaticUpdatesBypass, c.InventoryAtUtc, c.OsVersion, c.OsEditionId, c.OsProductName, c.EsuKey, c.AzureArc, c.Azure })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = clients.ToDictionary(c => c.Id, c => c.HostName);
        var security = baseline is null ? Security.SecuritySnapshot.Empty : await baseline.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var client in clients)
        {
            var path = $"servers/{client.Id}";
            if (client.LastHeartbeatAtUtc is { } heartbeat && now - heartbeat > TimeSpan.FromHours(settings.OfflineHours))
            {
                events.Add(new NotificationEvent($"server.offline:{client.Id}", NotificationKind.ServerOffline, $"{client.HostName} has sent no heartbeat since {Format(heartbeat)}.", path, client.HostName, "The server installs nothing new and reports nothing until the agent reaches the central server again."));
            }

            foreach (var check in client.HealthChecks.Where(h => h.Status == (int)HealthStatus.Failed))
            {
                events.Add(new NotificationEvent($"server.preflight:{client.Id}:{check.Name}", NotificationKind.PreFlightFailed, $"Pre-flight check {check.Name} failed on {client.HostName}: {check.Detail}".TrimEnd(' ', ':'), path + "#checks", client.HostName, "The server installs no updates until the check passes."));
            }

            if (client.RebootPending && client.LastBootAtUtc is { } boot && now - boot > TimeSpan.FromDays(settings.RebootPendingDays))
            {
                events.Add(new NotificationEvent($"server.reboot:{client.Id}", NotificationKind.RebootPendingLong, $"{client.HostName} has been waiting for a restart; it last started on {Format(boot)}.", path, client.HostName, "Installed updates take full effect only after the restart; further updates may wait for it."));
            }

            if (support?.Of(client.OsVersion, client.OsEditionId, client.OsProductName, Lifecycle.SupportLifecycle.Detected(client.EsuKey, client.Azure), security.Of(client.OsVersion)) is { } info && Lifecycle.SupportLifecycle.NeedsAttention(info.State))
            {
                events.Add(new NotificationEvent(
                    $"server.support:{client.Id}:{info.State}",
                    NotificationKind.SupportEnding,
                    $"{info.Name} on {client.HostName}: {Lifecycle.SupportLifecycle.Describe(info)}.",
                    Components.ServerPage.Path(client.Id, "system"),
                    client.HostName,
                    info.State switch
                    {
                        Lifecycle.SupportState.OutOfSupport => "Windows gets no security updates any more and no Extended Security Updates were found; plan an upgrade or ESU.",
                        Lifecycle.SupportState.Ended => "Without Extended Security Updates the server gets no security updates any more; ESU were not found yet.",
                        _ => "Plan the upgrade or Extended Security Updates before Windows stops getting security updates.",
                    }));
            }

            if (client.AutomaticUpdatesBypass)
            {
                events.Add(new NotificationEvent($"server.self-installing:{client.Id}", NotificationKind.InstalledOutside, $"Windows on {client.HostName} installs or restarts on its own, outside rings and maintenance windows.", path + "#checks", client.HostName, "Updates may arrive before the rings tested them and restarts may happen outside the maintenance windows."));
            }
        }

        if (deployment is not null)
        {
            var failed = (await deployment.GetAsync(null, null, null, cancellationToken).ConfigureAwait(false)).Where(e => e.State == Rollouts.DeploymentState.Failed).Take(200).ToList();
            var ids = failed.Select(e => e.UpdateId).Distinct().ToList();
            var titles = await db.Updates.AsNoTracking().Where(u => ids.Contains(u.UpdateId)).Select(u => new { u.UpdateId, u.Revision, u.KbArticle, u.Title, u.Classification }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in failed.Where(e => names.ContainsKey(e.ClientId)))
            {
                var update = titles.FirstOrDefault(u => u.UpdateId == entry.UpdateId && u.Revision == entry.Revision);
                if (Rollouts.DefinitionUpdates.IsClassification(update?.Classification))
                {
                    continue;
                }

                var name = update is null ? entry.UpdateId.ToString("D") : Rollouts.UpdateText.Title(update.KbArticle, update.Title);
                var code = entry.HResult ?? 0;
                events.Add(new NotificationEvent(
                    $"installation.failed:{entry.ClientId}:{entry.UpdateId}/{entry.Revision}",
                    NotificationKind.InstallationFailed,
                    string.Create(CultureInfo.InvariantCulture, $"{name} failed on {names[entry.ClientId]}: 0x{code:X8} {Agents.HResultCatalog.Describe(code)}"),
                    Components.ServerPage.Path(entry.ClientId, "history"),
                    $"{names[entry.ClientId]}, {name}",
                    "The server still misses the update; the agent tries again after the retry delay."));
            }
        }

        var since = now.AddDays(-1);
        var outside = await db.ClientUpdateHistory.AsNoTracking()
            .Where(h => h.DateUtc >= since && h.Operation == UpdateOperation.Installation && h.ClientApplicationId != UpdateHistoryEntry.AgentClientApplicationId
                && (h.ResultCode == InstallResultCode.Succeeded || h.ResultCode == InstallResultCode.SucceededWithErrors))
            .Select(h => new { h.ClientId, h.UpdateId, h.DateUtc, h.Title, h.ClientApplicationId })
            .Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await Rollouts.DefinitionUpdates.CatalogIdsAsync(db, outside.Select(h => h.UpdateId), cancellationToken).ConfigureAwait(false);
        events.AddRange(outside.Where(h => names.ContainsKey(h.ClientId) && !Rollouts.DefinitionUpdates.Is(h.UpdateId, h.Title, definitions)).Select(h => new NotificationEvent(
            // Keyed by what the installation is, not by the history row: the inventory replaces the rows, so an ID would
            // announce the same installation again after every inventory (Marc, 2026-10-28).
            string.Create(CultureInfo.InvariantCulture, $"installation.outside:{h.ClientId}:{h.UpdateId}:{h.DateUtc.Ticks}"),
            NotificationKind.InstalledOutside,
            $"{h.Title} was installed on {names[h.ClientId]} outside SUSyphos by {Agents.UpdateHistoryText.Source(h.ClientApplicationId)}.",
            Components.ServerPage.Path(h.ClientId, "windows-history"),
            names[h.ClientId],
            "The installation bypassed rings and maintenance windows.")));

        // Defender: servers that receive definition updates but got none for long, measured up to the last inventory
        // (it brings the history), so a long inventory interval or an offline server does not raise it.
        var definitionIds = db.Updates.Where(u => u.Classification == Rollouts.DefinitionUpdates.Classification).Select(u => u.UpdateId);
        var signatures = await db.ClientUpdateHistory.AsNoTracking()
            .Where(h => h.Operation == UpdateOperation.Installation
                && (h.ResultCode == InstallResultCode.Succeeded || h.ResultCode == InstallResultCode.SucceededWithErrors)
                && (definitionIds.Contains(h.UpdateId) || EF.Functions.Like(h.Title, Rollouts.DefinitionUpdates.TitlePatterns[0]) || EF.Functions.Like(h.Title, Rollouts.DefinitionUpdates.TitlePatterns[1])))
            .GroupBy(h => h.ClientId)
            .Select(g => new { ClientId = g.Key, Last = g.Max(h => h.DateUtc) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var signature in signatures.Where(s => names.ContainsKey(s.ClientId)))
        {
            var reference = clients.First(c => c.Id == signature.ClientId).InventoryAtUtc ?? now;
            if (reference - signature.Last > Rollouts.DefinitionUpdates.MaxSignatureAge)
            {
                events.Add(new NotificationEvent(
                    $"server.signatures:{signature.ClientId}",
                    NotificationKind.DefenderSignaturesOutdated,
                    $"{names[signature.ClientId]} has installed no antivirus definition update since {Format(signature.Last)}.",
                    Components.ServerPage.Path(signature.ClientId, "windows-history"),
                    names[signature.ClientId],
                    "The antivirus works with old signatures and may miss new threats; check Defender and the Windows Update history on the server."));
            }
        }
    }

    /// <summary>Things that happened in the last day: trust resets, administration changes, pauses and blackouts, rollouts reaching a ring.</summary>
    private async Task AddActivityEventsAsync(List<NotificationEvent> events, DateTime now, CancellationToken cancellationToken)
    {
        var since = now.AddDays(-1);
        var audits = await db.AuditEntries.AsNoTracking()
            .Where(a => a.AtUtc >= since && (a.Action == Agents.AgentEndpoints.TrustResetAuditAction || AdminActions.Contains(a.Action) || PauseActions.Contains(a.Action) || a.Action == "Installation.BlackoutAdded"))
            .Select(a => new { a.Id, a.Action, a.Actor, a.Target, a.Details })
            .Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var audit in audits)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"audit:{audit.Id}");
            if (audit.Action == Agents.AgentEndpoints.TrustResetAuditAction)
            {
                events.Add(new NotificationEvent(key, NotificationKind.TrustReset, $"The agent on {audit.Target} reset its trust in the central server locally and pinned it again.", null, audit.Target, "Expected after a planned reset; otherwise check who changed the registry value TrustResetToken on that server."));
            }
            else if (AdminActions.Contains(audit.Action))
            {
                events.Add(new NotificationEvent(key, NotificationKind.AdminChanged, $"{audit.Action}: {audit.Target} {audit.Details} (by {audit.Actor})".Replace("  ", " ", StringComparison.Ordinal), "administration/users", audit.Target));
            }
            else
            {
                var text = audit.Action switch
                {
                    "Installation.Paused" => $"All installations were paused by {audit.Actor}.",
                    "Installation.Resumed" => $"Installations run again (resumed by {audit.Actor}).",
                    _ => $"Blackout period added by {audit.Actor}: {audit.Details}".TrimEnd(' ', ':'),
                };
                events.Add(new NotificationEvent(key, NotificationKind.PauseChanged, text, "administration/maintenance"));
            }
        }

        var released = await (
                from state in db.RolloutRings.AsNoTracking()
                where state.ReleasedAtUtc >= since && state.State == RingRolloutState.Released
                join ring in db.Rings on state.RingId equals ring.Id
                join update in db.Updates on new { state.UpdateId, state.Revision } equals new { update.UpdateId, update.Revision }
                where update.Classification == null || update.Classification != Rollouts.DefinitionUpdates.Classification
                select new { state.UpdateId, state.Revision, state.RingId, RingName = ring.Name, update.KbArticle, update.Title })
            .Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        events.AddRange(released.Select(r => new NotificationEvent(
            $"rollout.ring:{r.UpdateId}/{r.Revision}/{r.RingId}",
            NotificationKind.RolloutProgress,
            $"{Rollouts.UpdateText.Title(r.KbArticle, r.Title)} was released to ring {r.RingName}.",
            $"updates/{r.UpdateId}/{r.Revision}",
            $"ring {r.RingName}")));

        // Manual remote actions that failed or finished with a result (Marc, 2026-10-27).
        var actions = await (
                from action in db.AgentActions.AsNoTracking()
                where action.CompletedAtUtc >= since && action.Kind != AgentActionKind.UninstallAgent
                join client in db.Clients on action.ClientId equals client.Id
                select new { action.Id, action.ClientId, client.HostName, action.Kind, action.State, action.RequestedBy, action.Detail })
            .Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var action in actions)
        {
            var label = Agents.AgentActionText.Label(action.Kind);
            if (Agents.ActionResults.IsFailure(action.State))
            {
                events.Add(new NotificationEvent(
                    $"action.failed:{action.Id}",
                    NotificationKind.RemoteActionFailed,
                    $"{label} on {action.HostName} ended with {action.State}{(action.Detail is { Length: > 0 } detail ? $": {detail}" : ".")}",
                    "approvals",
                    $"{action.HostName}, requested by {action.RequestedBy}"));
            }
            else if (action.State == AgentActionState.Succeeded && Agents.ActionResults.HasResult(action.Kind))
            {
                events.Add(new NotificationEvent(
                    $"action.finished:{action.Id}",
                    NotificationKind.RemoteActionFinished,
                    $"{label} on {action.HostName} finished{(action.Detail is { Length: > 0 } detail ? $": {detail}" : ".")}",
                    "approvals",
                    $"{action.HostName}, requested by {action.RequestedBy}"));
            }
        }
    }

    /// <summary>Once a day: new updates of the day before and the summary; on Mondays the weekly summary.</summary>
    private async Task AddDigestEventsAsync(List<NotificationEvent> events, DateTime now, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(now);
        var yesterday = today.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var start = today.ToDateTime(TimeOnly.MinValue);
        var fresh = await db.Updates.AsNoTracking().Where(u => u.FirstSeenAtUtc >= yesterday && u.FirstSeenAtUtc < start).Select(u => new { u.KbArticle, u.Title }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (fresh.Count > 0)
        {
            var names = string.Join("; ", fresh.Take(5).Select(u => Rollouts.UpdateText.Title(u.KbArticle, u.Title)));
            events.Add(new NotificationEvent(
                $"digest.new-updates:{today:yyyy-MM-dd}",
                NotificationKind.NewUpdatesDaily,
                $"{fresh.Count} new {(fresh.Count == 1 ? "update" : "updates")} reported yesterday: {names}{(fresh.Count > 5 ? " and more" : string.Empty)}.",
                "updates"));
        }

        var snapshot = await db.ComplianceSnapshots.AsNoTracking().Where(s => s.Day <= today).OrderByDescending(s => s.Day).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        var percent = snapshot.Servers == 0 ? 100 : 100 * snapshot.Compliant / snapshot.Servers;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.Servers} servers, patch compliance {percent} %, {snapshot.StatusFailed ?? 0} with failures, {snapshot.StatusOffline ?? 0} offline, {snapshot.OpenReleased ?? 0} released installations pending.");
        events.Add(new NotificationEvent($"digest.daily:{today:yyyy-MM-dd}", NotificationKind.DailySummary, "Daily summary: " + summary, string.Empty));
        if (today.DayOfWeek == DayOfWeek.Monday)
        {
            events.Add(new NotificationEvent($"digest.weekly:{today:yyyy-MM-dd}", NotificationKind.WeeklySummary, "Weekly summary: " + summary, string.Empty));
        }
    }

    private static string Format(DateTime utc) => SUSyphos.Server.Components.DisplayTime.Format(utc);
}
