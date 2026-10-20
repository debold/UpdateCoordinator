using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Notifications;

/// <summary>A condition that was already notified; removed when the condition ends, so it is notified again if it returns.</summary>
internal sealed class NotificationSentEntity
{
    public const int MaxKeyLength = 200;

    public required string Key { get; set; }

    public DateTime SentAtUtc { get; set; }
}

/// <summary>
/// Notifies administrators about new events (docs/06-dashboard.md, notifications): collects the current conditions,
/// sends the new ones in one message per enabled channel and remembers them. A failed delivery is retried on the next run.
/// </summary>
internal sealed partial class NotificationService(
    ServerDbContext db,
    NotificationEvents events,
    NotificationSettingsStore store,
    IEnumerable<INotificationChannel> channels,
    AuditLog audit,
    TimeProvider timeProvider,
    ILogger<NotificationService> logger)
{
    /// <summary>Upper bound of events in one message; the rest follows with the next run.</summary>
    public const int MaxEventsPerMessage = 50;

    /// <summary>Sends new events; returns how many were notified.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var current = await events.CollectAsync(cancellationToken).ConfigureAwait(false);
        var currentKeys = current.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var sent = await db.NotificationsSent.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Ended conditions are forgotten, so they are notified again if they return.
        db.NotificationsSent.RemoveRange(sent.Where(s => !currentKeys.Contains(s.Key)));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var sentKeys = sent.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var fresh = current.Where(e => !sentKeys.Contains(e.Key)).Take(MaxEventsPerMessage).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        var settings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var enabled = channels.Where(c => c.IsEnabled(settings)).ToList();
        if (enabled.Count == 0)
        {
            // Nothing configured: the events are not remembered, so they are sent once a channel is set up.
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var subject = fresh.Count == 1 ? $"SUSyphos: {fresh[0].Text}" : $"SUSyphos: {fresh.Count} new events";
        var message = new NotificationMessage(Truncate(subject, 200), fresh, now);
        var secrets = store.LoadSecrets();
        var delivered = false;
        foreach (var channel in enabled)
        {
            try
            {
                await channel.SendAsync(message, settings, secrets, cancellationToken).ConfigureAwait(false);
                delivered = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogDeliveryFailed(ex, channel.Name);
            }
        }

        // Remembered once at least one channel delivered; a channel that is down does not repeat the others every minute.
        if (!delivered)
        {
            return 0;
        }

        db.NotificationsSent.AddRange(fresh.Select(e => new NotificationSentEntity { Key = e.Key, SentAtUtc = now }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSent(fresh.Count);
        return fresh.Count;
    }

    /// <summary>Sends a test message through every enabled channel; returns an error text per failed channel.</summary>
    public async Task<Outcome> SendTestAsync(string actor, CancellationToken cancellationToken)
    {
        var settings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var enabled = channels.Where(c => c.IsEnabled(settings)).ToList();
        if (enabled.Count == 0)
        {
            return new Outcome("Enable e-mail or the webhook first.");
        }

        var message = new NotificationMessage(
            "SUSyphos: test notification",
            [new NotificationEvent("test", NotificationKind.ServerPendingApproval, "This is a test notification from the central server.", string.Empty)],
            timeProvider.GetUtcNow().UtcDateTime);
        var secrets = store.LoadSecrets();
        var errors = new List<string>();
        foreach (var channel in enabled)
        {
            try
            {
                await channel.SendAsync(message, settings, secrets, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogDeliveryFailed(ex, channel.Name);
                errors.Add($"{channel.Name}: {Truncate(ex.Message, 300)}");
            }
        }

        await audit.WriteAsync(actor, "Notifications.Tested", null, errors.Count == 0 ? "delivered" : string.Join("; ", errors), cancellationToken).ConfigureAwait(false);
        return errors.Count == 0 ? Outcome.Ok : new Outcome(string.Join(" ", errors));
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    [LoggerMessage(Level = LogLevel.Information, Message = "Notified {Count} new events.")]
    private partial void LogSent(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification through {Channel} failed; it is retried on the next run.")]
    private partial void LogDeliveryFailed(Exception exception, string channel);
}

/// <summary>Runs the notification check periodically.</summary>
internal sealed partial class NotificationWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<NotificationOptions> options,
    TimeProvider timeProvider,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CurrentValue.Interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (!options.CurrentValue.Enabled)
            {
                continue;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<NotificationService>().RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The notification check failed; it runs again with the next interval.")]
    private partial void LogFailed(Exception exception);
}
