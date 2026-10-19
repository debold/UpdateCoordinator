using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.Server.Trust;

namespace UpdateCoordinator.Server.Notifications;

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
}

/// <summary>
/// One condition worth a notification. <see cref="Key"/> identifies it: a notification is sent once while the condition
/// lasts and again only after it ended and came back.
/// </summary>
internal sealed record NotificationEvent(string Key, NotificationKind Kind, string Text, string? Path);

/// <summary>Derives the current notification conditions from the stored state, so no feature has to raise events itself.</summary>
internal sealed class NotificationEvents(
    ServerDbContext db,
    ITlsCertificateProvider certificates,
    IOptions<CertificateOptions> certificateOptions,
    TimeProvider timeProvider)
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

        var halted = await db.Groups.AsNoTracking().Where(g => g.LeasesHalted).Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        events.AddRange(halted.Select(g => new NotificationEvent($"group.halted:{g.Id}", NotificationKind.LeasesHalted, $"Installations in group {g.Name} stopped after failures; an operator must resume them.", "administration/groups")));

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

        return events;
    }
}
