using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Common;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Administration;

/// <summary>Where the backup password of the scheduled backup is kept. Bound from the "Backup" configuration section.</summary>
internal sealed class ScheduledBackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>DPAPI-protected file with the backup password; never part of a backup.</summary>
    public string PasswordPath { get; set; } = Path.Combine(DataPaths.Server, "backup-password.dat");

    /// <summary>How often the worker looks whether a backup is due.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>Settings of the scheduled backup, stored in the database (the password separately, protected).</summary>
internal sealed record ScheduledBackupSettings
{
    public const int MaxKeep = 365;

    public bool Enabled { get; init; }

    /// <summary>Absolute local or UNC folder the archives are written to.</summary>
    public string? Folder { get; init; }

    /// <summary>Time of day in server local time.</summary>
    public TimeSpan TimeOfDay { get; init; } = new(2, 0, 0);

    /// <summary>Number of archives kept; older scheduled archives in the folder are deleted.</summary>
    public int Keep { get; init; } = 14;
}

/// <summary>Result of the last scheduled backup.</summary>
internal sealed record ScheduledBackupRun(DateTime AtUtc, string? FileName, string? Error);

/// <summary>
/// Daily backup into a folder (docs/04-central-server.md, backup): the same archive as the dashboard download, encrypted
/// with a backup password that is stored DPAPI-protected on the central server and is not part of any backup. Old
/// scheduled archives beyond the configured number are deleted; files with other names are never touched.
/// </summary>
internal sealed partial class ScheduledBackup(
    ServerDbContext db,
    BackupService backups,
    IOptions<ScheduledBackupOptions> options,
    AuditLog audit,
    TimeProvider timeProvider,
    ILogger<ScheduledBackup> logger)
{
    public const string SettingsKey = "Backup.Schedule";
    public const string LastRunKey = "Backup.LastScheduledRun";
    public const string Actor = "scheduled backup";
    private const string FilePrefix = "susyphos-backup-scheduled-";

    public async Task<ScheduledBackupSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        await ReadAsync<ScheduledBackupSettings>(SettingsKey, cancellationToken).ConfigureAwait(false) ?? new ScheduledBackupSettings();

    public Task<ScheduledBackupRun?> GetLastRunAsync(CancellationToken cancellationToken) => ReadAsync<ScheduledBackupRun>(LastRunKey, cancellationToken);

    public bool HasPassword => PasswordFile().Load() is { Length: > 0 };

    /// <summary>Saves the settings; a null password keeps the stored one, a new one must meet the backup password rules.</summary>
    public async Task<Outcome> SaveAsync(ScheduledBackupSettings settings, string? password, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Keep is < 1 or > ScheduledBackupSettings.MaxKeep)
        {
            return new Outcome($"Keep between 1 and {ScheduledBackupSettings.MaxKeep} backups.");
        }

        if (settings.TimeOfDay < TimeSpan.Zero || settings.TimeOfDay >= TimeSpan.FromDays(1))
        {
            return new Outcome("The time must lie between 00:00 and 23:59.");
        }

        var folder = settings.Folder?.Trim();
        if (settings.Enabled || !string.IsNullOrEmpty(folder))
        {
            if (!IsValidFolder(folder))
            {
                return new Outcome(@"Enter an absolute folder, e.g. D:\Backup\SUSyphos or \\fileserver\backup\susyphos.");
            }
        }

        if (password is not null && BackupService.CheckPassword(password) is { } passwordError)
        {
            return new Outcome(passwordError);
        }

        if (settings.Enabled && password is null && !HasPassword)
        {
            return new Outcome("Enter a backup password for the scheduled backup.");
        }

        if (password is not null)
        {
            PasswordFile().Save(password);
        }

        await WriteAsync(SettingsKey, settings with { Folder = string.IsNullOrEmpty(folder) ? null : folder }, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Backup.ScheduleChanged", folder, settings.Enabled ? $"daily at {settings.TimeOfDay:hh\\:mm}, keep {settings.Keep}" : "off", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Runs the backup if it is enabled and due (once per day after the configured time). Returns true if it ran.</summary>
    public async Task<bool> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled || settings.Folder is null)
        {
            return false;
        }

        // All in server local clock time (DateTime), so the comparison never mixes offsets.
        var nowLocal = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone).DateTime;
        var dueLocal = nowLocal.Date + settings.TimeOfDay;
        var last = await GetLastRunAsync(cancellationToken).ConfigureAwait(false);
        var lastLocal = last is null ? (DateTime?)null : TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(last.AtUtc, DateTimeKind.Utc)), timeProvider.LocalTimeZone).DateTime;
        if (nowLocal < dueLocal || (lastLocal is { } previous && previous >= dueLocal))
        {
            return false;
        }

        await RunAsync(settings, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Creates one archive in the folder now and prunes old ones.</summary>
    public async Task<ScheduledBackupRun> RunAsync(ScheduledBackupSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        ScheduledBackupRun run;
        try
        {
            var password = PasswordFile().Load() ?? throw new InvalidOperationException("No backup password is stored.");
            var folder = settings.Folder ?? throw new InvalidOperationException("No folder is configured.");
            Directory.CreateDirectory(folder);
            var fileName = $"{FilePrefix}{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip";
            var target = Path.Combine(folder, fileName);
            var temporary = target + ".partial";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var outcome = await backups.CreateAsync(stream, password, Actor, cancellationToken).ConfigureAwait(false);
                    if (!outcome.Succeeded)
                    {
                        throw new InvalidOperationException(outcome.Error);
                    }
                }

                File.Move(temporary, target);
            }
            finally
            {
                File.Delete(temporary);
            }

            Prune(folder, settings.Keep);
            run = new ScheduledBackupRun(now, fileName, null);
            LogCreated(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            run = new ScheduledBackupRun(now, null, ex.Message.Length <= 300 ? ex.Message : ex.Message[..300]);
            LogFailed(ex);
        }

        await WriteAsync(LastRunKey, run, cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>An absolute local path (C:\…) or UNC path (\\server\share\…) without relative segments.</summary>
    internal static bool IsValidFolder(string? folder) =>
        folder is { Length: > 3 and <= 240 }
        && FolderRegex().IsMatch(folder)
        && !folder.Split('\\').Any(part => part is "." or "..")
        && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0
        && folder.IndexOfAny(['<', '>', '"', '|', '?', '*'], 2) < 0
        && folder.IndexOf(':', 2) < 0;

    /// <summary>Deletes the oldest scheduled archives beyond <paramref name="keep"/>; only files this backup wrote.</summary>
    internal static void Prune(string folder, int keep)
    {
        var archives = Directory.GetFiles(folder, $"{FilePrefix}*.zip")
            .Where(f => ArchiveNameRegex().IsMatch(Path.GetFileName(f)))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(keep);
        foreach (var archive in archives)
        {
            File.Delete(archive);
        }
    }

    private ProtectedJsonFile<string> PasswordFile() =>
        new(new ProtectedFile(options.Value.PasswordPath, "SUSyphos.Server.BackupPassword.v1", logger), logger);

    private async Task<T?> ReadAsync<T>(string key, CancellationToken cancellationToken)
        where T : class
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : JsonSerializer.Deserialize<T>(value);
    }

    private async Task WriteAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value);
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

    [GeneratedRegex(@"^([A-Za-z]:\\|\\\\[A-Za-z0-9._-]+\\[A-Za-z0-9$._ -]+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex FolderRegex();

    [GeneratedRegex(@"^susyphos-backup-scheduled-[0-9]{8}-[0-9]{6}\.zip$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ArchiveNameRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled backup written to {Path}.")]
    private partial void LogCreated(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduled backup failed; it is tried again with the next check.")]
    private partial void LogFailed(Exception exception);
}

/// <summary>Looks periodically whether the scheduled backup is due.</summary>
internal sealed partial class ScheduledBackupWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<ScheduledBackupOptions> options,
    TimeProvider timeProvider,
    ILogger<ScheduledBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CurrentValue.CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ScheduledBackup>().RunIfDueAsync(stoppingToken).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Checking the scheduled backup failed.")]
    private partial void LogFailed(Exception exception);
}
