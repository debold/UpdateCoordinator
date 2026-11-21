using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Common.Logging;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Administration;

/// <summary>The debug mode of a server or of the central server: its end and who switched it on.</summary>
internal sealed record DebugState(DateTime UntilUtc, string By);

/// <summary>
/// Debug mode (Marc, 2026-11-21): an administrator switches it on for an agent or for the central server for a while, to
/// collect what the normal logs leave out. The agent then logs at Debug, WinGet writes its verbose log and a log
/// collection adds the Windows Update, CBS and DISM logs; the central server logs at Debug, including every request to its
/// WinGet source. It ends by itself at its end time, can be switched off or extended earlier, and every change is in the
/// audit log. Durations are limited to <see cref="MaxHours"/>.
/// </summary>
internal sealed class DebugMode(ServerDbContext db, AuditLog audit, TimeProvider timeProvider, FileLogLevel? fileLevel = null)
{
    /// <summary>Durations the dialogs offer, in hours; <see cref="DefaultHours"/> is preselected.</summary>
    public static readonly IReadOnlyList<int> Durations = [1, 4, 24, 72];

    public const int DefaultHours = 24;

    public const int MaxHours = 72;

    /// <summary>The debug mode of a server; null when it is off or over.</summary>
    public async Task<DebugState?> ServerAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var now = Now;
        var client = await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => new { c.DebugUntilUtc, c.DebugBy }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return client?.DebugUntilUtc is { } until && until > now ? new DebugState(until, client.DebugBy ?? "?") : null;
    }

    /// <summary>Servers in debug mode now, with host name and state.</summary>
    public async Task<IReadOnlyList<(Guid ClientId, string HostName, DebugState State)>> ServersAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        return [.. (await db.Clients.AsNoTracking().Where(c => c.DebugUntilUtc != null && c.DebugUntilUtc > now).Select(c => new { c.Id, c.HostName, c.DebugUntilUtc, c.DebugBy }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(c => (c.Id, c.HostName, new DebugState(c.DebugUntilUtc!.Value, c.DebugBy ?? "?")))
            .OrderBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Switches the debug mode of a server on for <paramref name="hours"/> (null: off). The agent takes it with its next heartbeat.</summary>
    public async Task<Outcome> SetServerAsync(Guid clientId, int? hours, string actor, CancellationToken cancellationToken)
    {
        if (hours is { } h && (h < 1 || h > MaxHours))
        {
            return new Outcome($"The debug mode lasts 1 to {MaxHours} hours.");
        }

        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return new Outcome("The server does not exist.");
        }

        client.DebugUntilUtc = hours is { } duration ? Now.AddHours(duration) : null;
        client.DebugBy = hours is null ? null : actor;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, hours is null ? "Debug.ServerOff" : "Debug.ServerOn", client.HostName, hours is { } on ? string.Create(CultureInfo.InvariantCulture, $"for {on} hours, until {client.DebugUntilUtc:yyyy-MM-dd HH:mm} UTC") : null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>The debug mode of the central server; null when it is off or over.</summary>
    public async Task<DebugState?> CentralAsync(CancellationToken cancellationToken)
    {
        var settings = await db.Settings.AsNoTracking().Where(s => s.Key == SettingEntity.DebugUntil || s.Key == SettingEntity.DebugBy).ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken).ConfigureAwait(false);
        return settings.GetValueOrDefault(SettingEntity.DebugUntil) is { } text
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
            && until > Now
                ? new DebugState(until, settings.GetValueOrDefault(SettingEntity.DebugBy) ?? "?")
                : null;
    }

    /// <summary>Switches the debug mode of the central server on for <paramref name="hours"/> (null: off) and sets the file log level at once.</summary>
    public async Task<Outcome> SetCentralAsync(int? hours, string actor, CancellationToken cancellationToken)
    {
        if (hours is { } h && (h < 1 || h > MaxHours))
        {
            return new Outcome($"The debug mode lasts 1 to {MaxHours} hours.");
        }

        await db.Settings.Where(s => s.Key == SettingEntity.DebugUntil || s.Key == SettingEntity.DebugBy).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        DateTime? until = hours is { } duration ? Now.AddHours(duration) : null;
        if (until is { } end)
        {
            db.Settings.Add(new SettingEntity { Key = SettingEntity.DebugUntil, Value = end.ToString("O", CultureInfo.InvariantCulture) });
            db.Settings.Add(new SettingEntity { Key = SettingEntity.DebugBy, Value = actor });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        fileLevel?.SetDebug(until is not null);
        await audit.WriteAsync(actor, hours is null ? "Debug.CentralOff" : "Debug.CentralOn", "central server", until is { } on ? string.Create(CultureInfo.InvariantCulture, $"for {hours} hours, until {on:yyyy-MM-dd HH:mm} UTC") : null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Sets the file log level of the central server to what the stored debug mode says (start, expiry); true when it changed.</summary>
    public async Task<bool> SyncCentralLevelAsync(CancellationToken cancellationToken)
    {
        var active = await CentralAsync(cancellationToken).ConfigureAwait(false) is not null;
        return fileLevel?.SetDebug(active) == true;
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;
}

/// <summary>Ends the debug mode of the central server at its end time and restores it after a restart.</summary>
internal sealed partial class DebugModeWorker(IServiceScopeFactory scopes, ILogger<DebugModeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<DebugMode>().SyncCentralLevelAsync(stoppingToken).ConfigureAwait(false))
                {
                    LogChanged();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Debug mode of the central server changed; the file log level follows it.")]
    private partial void LogChanged();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The debug mode of the central server could not be checked.")]
    private partial void LogFailed(Exception exception);
}
