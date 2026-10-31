using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.AgentPackages;

/// <summary>Result of a check for new agent and central server versions.</summary>
internal sealed record AgentPackageCheck(DateTime AtUtc, string? AddedVersion, string? Error, string? ServerVersion = null)
{
    public string Describe() => Error is not null
        ? $"failed: {Error}"
        : (AddedVersion is null ? "no newer agent version" : $"agent {AddedVersion} downloaded, waiting for release")
            + (ServerVersion is null ? string.Empty : $"; central server {ServerVersion} available");
}

/// <summary>A central server release newer than the running one: version, release page and MSI on GitHub.</summary>
public sealed record ServerUpdate(string Version, string? Page, string? Package, DateTime FoundAtUtc);

/// <summary>The version of this central server; replaced in tests.</summary>
internal interface IRunningServerVersion
{
    string Current { get; }
}

internal sealed class RunningServerVersion : IRunningServerVersion
{
    public string Current => Administration.ServerVersion.Display;
}

/// <summary>
/// Checks for new agent versions and releases packages ring by ring (docs/04-central-server.md, agent updates).
/// A new version is only downloaded and verified; agents update after an administrator released it.
/// </summary>
internal sealed partial class AgentPackageAdministration(
    ServerDbContext db,
    AgentPackageStore store,
    IAgentReleaseSource releases,
    IRunningServerVersion running,
    IOptions<AgentPackageOptions> options,
    AuditLog audit,
    TimeProvider timeProvider,
    ILogger<AgentPackageAdministration> logger)
{
    public const string LastCheckSetting = "AgentPackages.LastCheck";

    /// <summary>Setting with the newest central server release that is newer than the running version; absent if none.</summary>
    public const string ServerUpdateSetting = "ServerUpdate.Available";

    private const int MaxChecksumFileSize = 64 * 1024;

    public enum ReleaseScope
    {
        NextRing,
        AllRings,
    }

    /// <summary>
    /// Looks for a release newer than every known agent package, downloads it and verifies it against the published checksum,
    /// and notes a central server release newer than the running version (installed by an administrator, never automatically).
    /// </summary>
    public async Task<AgentPackageCheck> CheckAsync(string? actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        AgentPackageCheck result;
        try
        {
            var listed = await releases.ListAsync(cancellationToken).ConfigureAwait(false);
            await ReleaseNotes.SaveAsync(db, listed, cancellationToken).ConfigureAwait(false);
            var candidates = listed
                .Where(r => options.Value.IncludePrereleases || !r.Prerelease && r.Version.Prerelease is null)
                .ToList();
            var server = await NoteServerUpdateAsync(candidates, now, cancellationToken).ConfigureAwait(false);
            result = new AgentPackageCheck(now, await DownloadNewestAsync(candidates, cancellationToken).ConfigureAwait(false), null, server);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or InvalidDataException or ArgumentException)
        {
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogCheckFailed(ex);
            result = new AgentPackageCheck(now, null, ex is HttpRequestException { StatusCode: { } status } ? $"GitHub answered {(int)status}" : ex.GetType().Name);
        }

        await SaveLastCheckAsync(result, cancellationToken).ConfigureAwait(false);
        if (actor is not null)
        {
            await audit.WriteAsync(actor, "AgentPackage.Checked", options.Value.Repository, result.Describe(), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<AgentPackageCheck?> GetLastCheckAsync(CancellationToken cancellationToken)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == LastCheckSetting).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : System.Text.Json.JsonSerializer.Deserialize<AgentPackageCheck>(value);
    }

    /// <summary>Releases a package for the next ring (the first one if none yet) or for all rings at once.</summary>
    public async Task<Outcome> ReleaseAsync(string? version, ReleaseScope scope, string actor, CancellationToken cancellationToken)
    {
        var package = await db.AgentPackages.SingleOrDefaultAsync(p => p.Version == version, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return new Outcome("The agent version does not exist.");
        }

        if (package.ReleasedThroughRingOrder == AgentPackageEntity.AllRings)
        {
            return new Outcome("The agent version is already released for all rings.");
        }

        var orders = await db.Rings.AsNoTracking().OrderBy(r => r.Order).Select(r => r.Order).ToListAsync(cancellationToken).ConfigureAwait(false);
        var next = scope == ReleaseScope.AllRings
            ? AgentPackageEntity.AllRings
            : orders.Where(o => package.ReleasedThroughRingOrder is not { } current || o > current).Cast<int?>().FirstOrDefault() ?? AgentPackageEntity.AllRings;
        // The last ring reached means all rings, so rings added later are covered too.
        if (orders.Count > 0 && next >= orders[^1])
        {
            next = AgentPackageEntity.AllRings;
        }

        package.ReleasedThroughRingOrder = next;
        package.ReleasedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        package.ReleasedBy = actor;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var ringName = next == AgentPackageEntity.AllRings
            ? "all rings"
            : await db.Rings.AsNoTracking().Where(r => r.Order == next).Select(r => r.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "AgentPackage.Released", package.Version, $"released through {ringName}", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Stops further updates to the version; agents already on it stay there.</summary>
    public async Task<Outcome> WithdrawAsync(string? version, string actor, CancellationToken cancellationToken)
    {
        var package = await db.AgentPackages.SingleOrDefaultAsync(p => p.Version == version, cancellationToken).ConfigureAwait(false);
        if (package?.ReleasedThroughRingOrder is null)
        {
            return new Outcome("The agent version is not released.");
        }

        package.ReleasedThroughRingOrder = null;
        package.ReleasedAtUtc = null;
        package.ReleasedBy = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "AgentPackage.Withdrawn", package.Version, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>The newest package released for a ring (by its order), or null.</summary>
    public static AgentPackageEntity? TargetFor(IEnumerable<AgentPackageEntity> packages, int ringOrder) =>
        AgentPackageStore.Newest(packages.Where(p => p.ReleasedThroughRingOrder >= ringOrder));

    /// <summary>The newest central server release newer than the running version, or null.</summary>
    public async Task<ServerUpdate?> GetServerUpdateAsync(CancellationToken cancellationToken)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == ServerUpdateSetting).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var update = value is null ? null : System.Text.Json.JsonSerializer.Deserialize<ServerUpdate>(value);

        // Gone once the central server runs that version (or a newer one), even before the next check.
        return update is not null && AgentVersion.TryParse(update.Version, out var offered) && IsNewerThanRunning(offered) ? update : null;
    }

    private bool IsNewerThanRunning(AgentVersion version) =>
        !AgentVersion.TryParse(running.Current, out var current) || version > current;

    /// <summary>Stores the newest central server release newer than the running version, or removes the note.</summary>
    private async Task<string?> NoteServerUpdateAsync(IReadOnlyList<AgentRelease> candidates, DateTime now, CancellationToken cancellationToken)
    {
        var newest = candidates.Where(r => r.ServerPackage is not null).OrderByDescending(r => r.Version).FirstOrDefault();
        var update = newest is not null && IsNewerThanRunning(newest.Version)
            ? new ServerUpdate(newest.Version.ToString(), newest.Page?.ToString(), newest.ServerPackage!.ToString(), now)
            : null;
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == ServerUpdateSetting, cancellationToken).ConfigureAwait(false);
        if (update is null)
        {
            if (setting is not null)
            {
                db.Settings.Remove(setting);
            }
        }
        else
        {
            // Keep the time it was found first, so the dashboard shows since when it waits.
            var previous = setting is null ? null : System.Text.Json.JsonSerializer.Deserialize<ServerUpdate>(setting.Value);
            var value = System.Text.Json.JsonSerializer.Serialize(previous?.Version == update.Version ? update with { FoundAtUtc = previous.FoundAtUtc } : update);
            if (setting is null)
            {
                db.Settings.Add(new SettingEntity { Key = ServerUpdateSetting, Value = value });
            }
            else
            {
                setting.Value = value;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return update?.Version;
    }

    private async Task<string?> DownloadNewestAsync(IReadOnlyList<AgentRelease> candidates, CancellationToken cancellationToken)
    {
        var known = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var newestKnown = AgentPackageStore.Newest(known) is { } package && AgentVersion.TryParse(package.Version, out var version) ? version : null;
        var candidate = candidates.OrderByDescending(r => r.Version).FirstOrDefault();
        if (candidate is null || newestKnown is not null && candidate.Version <= newestKnown)
        {
            return null;
        }

        var expected = await ReadChecksumAsync(candidate, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The checksum file lists no hash for the agent package.");
        await using var content = await releases.OpenAsync(candidate.Package, cancellationToken).ConfigureAwait(false);
        return await store.AddAsync(candidate.Version, AgentPackageSource.GitHub, content, expected, cancellationToken).ConfigureAwait(false)
            ? candidate.Version.ToString()
            : throw new InvalidDataException("The downloaded agent package does not match its checksum or is too large.");
    }

    /// <summary>Reads "&lt;sha256&gt;  &lt;file name&gt;" lines and returns the hash of the agent package.</summary>
    private async Task<string?> ReadChecksumAsync(AgentRelease release, CancellationToken cancellationToken)
    {
        await using var stream = await releases.OpenAsync(release.Checksums, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxChecksumFileSize + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        if (total > MaxChecksumFileSize)
        {
            throw new InvalidDataException("The checksum file is too large.");
        }

        return ParseChecksum(Encoding.UTF8.GetString(buffer, 0, total), AgentPackageStore.FileNameFor(release.Version));
    }

    internal static string? ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    private async Task SaveLastCheckAsync(AgentPackageCheck result, CancellationToken cancellationToken)
    {
        var value = System.Text.Json.JsonSerializer.Serialize(result);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == LastCheckSetting, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = LastCheckSetting, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking for new agent versions failed; the next check runs with the next interval.")]
    private partial void LogCheckFailed(Exception exception);
}

/// <summary>Registers the bundled agent package at start and checks for new versions periodically.</summary>
internal sealed partial class AgentPackageWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<AgentPackageOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentPackageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgentPackageStore>().RegisterBundledAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogBundledFailed(ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.CheckEnabled)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AgentPackageAdministration>().CheckAsync(null, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(ex);
            }

            // Waits the full interval after a failure too, so a persistent error never turns into a busy loop.
            try
            {
                await Task.Delay(options.CurrentValue.CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Registering the agent package shipped with the central server failed.")]
    private partial void LogBundledFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The agent package check failed; it runs again later.")]
    private partial void LogFailed(Exception exception);
}
