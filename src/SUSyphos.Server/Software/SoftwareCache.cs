using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Amsi;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>What happens with an installer the virus scan does not pass.</summary>
internal enum ScanMode
{
    /// <summary>Installers are not scanned.</summary>
    Off,

    /// <summary>Installers are scanned and findings logged, but offered anyway.</summary>
    Audit,

    /// <summary>A finding, a failed scan or a file too large to scan keeps the version out of the source (default).</summary>
    Enforce,
}

/// <summary>Scans an installer file. Separate so tests decide what the scanner says.</summary>
internal interface IInstallerScanner
{
    ScanResult Scan(string path, long maxBytes);
}

/// <summary>The AMSI scanner of the machine, started once; without a registered provider every scan says "unavailable".</summary>
internal sealed class AmsiInstallerScanner : IInstallerScanner, IDisposable
{
    private readonly Lazy<AmsiScanner> _scanner = new(() => new AmsiScanner("SUSyphos"));

    public ScanResult Scan(string path, long maxBytes) => _scanner.Value.ScanFile(path, maxBytes);

    public void Dispose()
    {
        if (_scanner.IsValueCreated)
        {
            _scanner.Value.Dispose();
        }
    }
}

/// <summary>
/// The installers the WinGet source of the central server offers (folder <c>cache</c> under
/// <see cref="SoftwareOptions.Directory"/>): only versions whose download matched the SHA-256 of the manifest and passed
/// the virus scan get here. Shared by the source routes and the cache service.
/// </summary>
internal sealed partial class SoftwareSourceCache(IOptions<SoftwareOptions> options, ILogger<SoftwareSourceCache> logger)
{
    /// <summary>Path of the REST source below the central server's address.</summary>
    public const string ApiPath = "/winget/api/";

    /// <summary>Path of the installer files below the central server's address.</summary>
    public const string FilesPath = "/winget/files/";

    private readonly Lock _gate = new();
    private PackageCache? _cache;

    public string Folder => Path.Combine(options.Value.Directory, "cache");

    public string FilesFolder => Path.Combine(Folder, PackageCache.FilesFolderName);

    /// <summary>The offered versions; opened once and checked against the SHA-256 of every file.</summary>
    public PackageCache Cache
    {
        get
        {
            lock (_gate)
            {
                if (_cache is null)
                {
                    var problems = new List<string>();
                    _cache = PackageCache.Open(Folder, problems, verifyFiles: true);
                    foreach (var problem in problems)
                    {
                        LogDropped(problem);
                    }
                }

                return _cache;
            }
        }
    }

    /// <summary>Changes the offered versions under the lock, so a request never sees a half-written index.</summary>
    public T Change<T>(Func<PackageCache, T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            return change(Cache);
        }
    }

    /// <summary>The responder for one request; installer addresses point at the address the request came in on.</summary>
    public RestSourceResponder Responder(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return new RestSourceResponder(Cache, new Uri($"{request.Scheme}://{request.Host}{FilesPath}"));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WinGet source: {Problem} (not offered).")]
    private partial void LogDropped(string problem);
}

/// <summary>
/// Fills the installer cache (docs/04-central-server.md, section 7c): a software version is downloaded only once its
/// rollout started, checked against the SHA-256 of the manifest, scanned and only then offered by the WinGet source.
/// Per package the newest two offered versions are kept (downgrade), plus every version a rollout still uses.
/// </summary>
internal sealed partial class SoftwareCacheService(
    ServerDbContext db,
    ISoftwareSource source,
    IInstallerScanner scanner,
    SoftwareSourceCache served,
    AuditLog audit,
    IOptions<SoftwareOptions> options,
    TimeProvider timeProvider,
    ILogger<SoftwareCacheService> logger)
{
    /// <summary>Caches every version with a running rollout that is not offered yet; returns how many became available.</summary>
    public async Task<int> CacheReleasedAsync(CancellationToken cancellationToken)
    {
        var active = db.Rollouts.Where(r => r.State == RolloutState.Active).Select(r => r.UpdateId);
        var pending = await db.PackageVersions
            .Where(v => v.CachedAtUtc == null && v.ScanState != InstallerScanState.Detected && active.Contains(v.UpdateId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var offered = 0;
        foreach (var version in pending)
        {
            try
            {
                if (await CacheAsync(version, cancellationToken).ConfigureAwait(false))
                {
                    offered++;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or ArgumentException or TaskCanceledException or UnauthorizedAccessException)
            {
                // Tried again with the next run; nothing is offered.
                version.ScanDetail = Truncate($"Download failed: {exception.Message}");
                LogDownloadFailed(version.PackageId, version.Version, exception.Message);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await CleanUpAsync(cancellationToken).ConfigureAwait(false);
        return offered;
    }

    private async Task<bool> CacheAsync(PackageVersionEntity version, CancellationToken cancellationToken)
    {
        var staging = PackageCache.Open(Path.Combine(options.Value.Directory, "staging"), [], verifyFiles: false);
        var cached = await source.CacheInstallersAsync(staging, version.PackageId, version.Version, cancellationToken).ConfigureAwait(false);
        var files = cached.Installers.Select(i => i["CachedFile"]?.GetValue<string>()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mode = options.Value.ScanMode;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (state, detail) = (InstallerScanState.NotScanned, (string?)null);
        if (mode != ScanMode.Off)
        {
            (state, detail) = Scan(files.Select(f => Path.Combine(staging.FilesFolder, f)));
            version.ScannedAtUtc = now;
        }

        version.ScanState = state;
        version.ScanDetail = Truncate(detail);
        var blocked = mode == ScanMode.Enforce && state is InstallerScanState.Detected or InstallerScanState.Failed;
        if (blocked)
        {
            staging.Remove(version.PackageId, version.Version);
            if (state == InstallerScanState.Detected)
            {
                LogDetected(version.PackageId, version.Version, detail!);
                await audit.WriteAsync("system", "Package.VirusDetected", $"{version.PackageId} {version.Version}", detail, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                LogNotScanned(version.PackageId, version.Version, detail!);
            }

            return false;
        }

        // Moved, not copied: staging holds nothing that is offered.
        Directory.CreateDirectory(served.FilesFolder);
        foreach (var file in files)
        {
            File.Move(Path.Combine(staging.FilesFolder, file), Path.Combine(served.FilesFolder, file), overwrite: true);
        }

        served.Change(cache =>
        {
            cache.Put(cached);
            return true;
        });
        staging.Remove(version.PackageId, version.Version);
        var first = cached.Installers[0];
        version.InstallerFile = first["CachedFile"]?.GetValue<string>();
        version.InstallerSha256 = first["InstallerSha256"]?.GetValue<string>()?.ToLowerInvariant();
        version.SizeBytes = files.Sum(f => new FileInfo(Path.Combine(served.FilesFolder, f)).Length);
        version.CachedAtUtc = now;
        LogCached(version.PackageId, version.Version, files.Count, state);
        return true;
    }

    /// <summary>The worst result over all installer files of a version.</summary>
    private (InstallerScanState State, string? Detail) Scan(IEnumerable<string> paths)
    {
        var maxBytes = (long)options.Value.MaxScanMegabytes * 1024 * 1024;
        var results = paths.Select(path => (Path.GetFileName(path), scanner.Scan(path, maxBytes))).ToList();
        if (results.FirstOrDefault(r => r.Item2.Outcome == ScanOutcome.Detected) is { Item2: not null } detected)
        {
            return (InstallerScanState.Detected, $"{detected.Item1}: {detected.Item2.Detail}");
        }

        if (results.FirstOrDefault(r => r.Item2.Outcome is ScanOutcome.Failed or ScanOutcome.TooLarge) is { Item2: not null } failed)
        {
            return (InstallerScanState.Failed, $"{failed.Item1}: {failed.Item2.Detail}");
        }

        // Without a registered antivirus product nothing was scanned; the version is offered, the dashboard warns.
        return results.Any(r => r.Item2.Outcome == ScanOutcome.Unavailable)
            ? (InstallerScanState.NotScanned, results.First(r => r.Item2.Outcome == ScanOutcome.Unavailable).Item2.Detail)
            : (InstallerScanState.Clean, null);
    }

    /// <summary>Keeps per package the newest two offered versions and every version a rollout still uses; removes the rest.</summary>
    private async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        var activeIds = (await db.Rollouts.Where(r => r.State == RolloutState.Active).Select(r => r.UpdateId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var cachedVersions = await db.PackageVersions.Where(v => v.CachedAtUtc != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var package in cachedVersions.GroupBy(v => v.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var keep = package.OrderByDescending(v => v.Version, VersionComparer.Instance).Take(2).ToHashSet();
            foreach (var version in package.Where(v => !keep.Contains(v) && !activeIds.Contains(v.UpdateId)))
            {
                served.Change(cache => cache.Remove(version.PackageId, version.Version));
                version.CachedAtUtc = null;
                version.InstallerFile = null;
                LogRemoved(version.PackageId, version.Version);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Truncate(string? text) => text is null ? null : text.Length <= 512 ? text : text[..512];

    [LoggerMessage(Level = LogLevel.Information, Message = "WinGet source: {PackageId} {Version} cached ({Files} installer files, scan {State}) and offered.")]
    private partial void LogCached(string packageId, string version, int files, InstallerScanState state);

    [LoggerMessage(Level = LogLevel.Error, Message = "WinGet source: the virus scan found a threat in {PackageId} {Version} ({Detail}); the version is not offered and its files were deleted.")]
    private partial void LogDetected(string packageId, string version, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WinGet source: {PackageId} {Version} could not be scanned ({Detail}); it is not offered.")]
    private partial void LogNotScanned(string packageId, string version, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WinGet source: downloading {PackageId} {Version} failed ({Reason}); tried again with the next run.")]
    private partial void LogDownloadFailed(string packageId, string version, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "WinGet source: {PackageId} {Version} removed from the cache (older than the two newest).")]
    private partial void LogRemoved(string packageId, string version);
}

/// <summary>Fills the installer cache every few minutes, so a started rollout soon has its installer.</summary>
internal sealed partial class SoftwareCacheWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<SoftwareOptions> options,
    TimeProvider timeProvider,
    ILogger<SoftwareCacheWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CurrentValue.CacheInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                if (options.CurrentValue.CheckEnabled)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (await SoftwareFeature.IsEnabledAsync(scope.ServiceProvider.GetRequiredService<ServerDbContext>(), stoppingToken).ConfigureAwait(false))
                    {
                        await scope.ServiceProvider.GetRequiredService<SoftwareCacheService>().CacheReleasedAsync(stoppingToken).ConfigureAwait(false);
                    }
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
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Filling the WinGet installer cache failed; it runs again later.")]
    private partial void LogFailed(Exception exception);
}
