using System.Diagnostics;

using Microsoft.Management.Deployment;

namespace SUSyphos.WinGet;

/// <summary>A source WinGet is configured with.</summary>
public sealed record WinGetSource(string Name, string Argument, string Type, string? Id, DateTimeOffset? LastUpdate);

/// <summary>A package a search found.</summary>
public sealed record WinGetFoundPackage(string Id, string Name, string? DefaultVersion, string? InstalledVersion, IReadOnlyList<string> Versions);

/// <summary>Outcome of an install or upgrade.</summary>
/// <param name="Operation">install or upgrade.</param>
/// <param name="PackageId">WinGet id of the package.</param>
/// <param name="Version">Version asked for, or the source's default version.</param>
/// <param name="Status">WinGet's result status (e.g. Ok, InstallError, NoApplicableUpgrade).</param>
/// <param name="ExtendedError">HRESULT and message WinGet gives on failure.</param>
/// <param name="InstallerErrorCode">Exit code of the installer.</param>
/// <param name="RebootRequired">True when the installer asked for a restart (which nobody triggers).</param>
/// <param name="Duration">Time from the call to the result.</param>
/// <param name="LogFile">Log file WinGet wrote.</param>
public sealed record WinGetOperationResult(
    string Operation,
    string PackageId,
    string? Version,
    string Status,
    string? ExtendedError,
    uint InstallerErrorCode,
    bool RebootRequired,
    TimeSpan Duration,
    string LogFile)
{
    public bool Succeeded => Status == nameof(InstallResultStatus.Ok);
}

/// <summary>Thrown when WinGet answers an operation with an error status before installing anything.</summary>
public sealed class WinGetException(string message) : Exception(message);

/// <summary>
/// The operations the probe and later the agent need from WinGet: sources, search, installed list, install and upgrade
/// for the whole machine, silently. Agreements of a package are only accepted when the caller says so explicitly.
/// </summary>
public sealed class WinGetClient(WinGetEngine engine)
{
    /// <summary>Name under which a private REST source is added for <see cref="UseSourceAsync"/>.</summary>
    public const string PrivateSourceName = "susyphos";

    private readonly PackageManager _manager = engine.CreatePackageManager();

    public IReadOnlyList<WinGetSource> Sources() =>
        _manager.GetPackageCatalogs()
            .Select(reference => reference.Info)
            .Select(info => new WinGetSource(info.Name, info.Argument, info.Type, info.Id, info.LastUpdateTime == default ? null : info.LastUpdateTime))
            .ToList();

    /// <summary>
    /// Adds a private REST source under <see cref="PrivateSourceName"/> (machine-wide for the account, as winget
    /// source add does) unless it is already there with the same address, and returns the reference to it.
    /// </summary>
    public async Task<PackageCatalogReference> UseSourceAsync(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A WinGet REST source must use https.", nameof(address));
        }

        var existing = _manager.GetPackageCatalogByName(PrivateSourceName);
        if (existing is not null && string.Equals(existing.Info.Argument, address.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        if (existing is not null)
        {
            throw new WinGetException($"A source named {PrivateSourceName} already points at {existing.Info.Argument}; remove it first (winget source remove {PrivateSourceName}).");
        }

        var options = engine.CreateAddCatalogOptions();
        options.Name = PrivateSourceName;
        options.SourceUri = address.ToString();
        options.Type = "Microsoft.Rest";
        var result = await _manager.AddPackageCatalogAsync(options);
        if (result.Status != AddPackageCatalogStatus.Ok)
        {
            throw new WinGetException($"Adding the source failed: {result.Status} {Describe(result.ExtendedErrorCode)}");
        }

        return _manager.GetPackageCatalogByName(PrivateSourceName) ?? throw new WinGetException("The source was added but cannot be found.");
    }

    public async Task<IReadOnlyList<WinGetFoundPackage>> SearchAsync(string text, PackageCatalogReference? source = null, int limit = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var catalog = await ConnectAsync(source ?? _manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog));
        var options = engine.CreateFindPackagesOptions();
        options.ResultLimit = (uint)limit;
        var filter = engine.CreatePackageMatchFilter();
        filter.Field = PackageMatchField.CatalogDefault;
        filter.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
        filter.Value = text;
        options.Selectors.Add(filter);
        return (await FindAsync(catalog, options))
            .Select(package => new WinGetFoundPackage(
                package.Id,
                package.Name,
                package.DefaultInstallVersion?.Version,
                package.InstalledVersion?.Version,
                package.AvailableVersions.Select(version => version.Version).ToList()))
            .ToList();
    }

    /// <summary>
    /// The installed list as WinGet sees it, each program correlated with the sources where WinGet finds it.
    /// </summary>
    public async Task<IReadOnlyList<WinGetInstalledPackage>> InstalledAsync()
    {
        var catalog = await ConnectAsync(Composite(null, CompositeSearchBehavior.LocalCatalogs));
        return (await FindAsync(catalog, engine.CreateFindPackagesOptions()))
            .Select(package =>
            {
                var installed = package.InstalledVersion;
                var available = package.DefaultInstallVersion;
                return new WinGetInstalledPackage(
                    package.Id,
                    package.Name,
                    installed?.Version,
                    available?.Version,
                    available?.PackageCatalog?.Info.Name,
                    installed?.ProductCodes.ToList() ?? [],
                    installed?.DisplayName,
                    Metadata(installed, PackageVersionMetadataField.InstalledScope),
                    installed?.PackageFamilyNames.ToList(),
                    package.IsUpdateAvailable);
            })
            .ToList();
    }

    public Task<WinGetOperationResult> InstallAsync(string id, string? version, PackageCatalogReference? source, bool acceptAgreements, string logFolder) =>
        RunAsync("install", id, version, source, acceptAgreements, logFolder);

    public Task<WinGetOperationResult> UpgradeAsync(string id, string? version, PackageCatalogReference? source, bool acceptAgreements, string logFolder) =>
        RunAsync("upgrade", id, version, source, acceptAgreements, logFolder);

    /// <summary>Uninstalls an installed package for the machine, silently.</summary>
    public async Task<WinGetOperationResult> UninstallAsync(string id, PackageCatalogReference? source, string logFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFolder);
        Directory.CreateDirectory(logFolder);
        var package = await FindByIdAsync(Composite(source, CompositeSearchBehavior.LocalCatalogs), id);
        if (package.InstalledVersion is null)
        {
            throw new WinGetException($"{id} is not installed.");
        }

        var options = engine.CreateUninstallOptions();
        options.PackageUninstallScope = PackageUninstallScope.System;
        options.PackageUninstallMode = PackageUninstallMode.Silent;
        var logFile = LogFile(logFolder, "uninstall", id);
        options.LogOutputPath = logFile;
        var watch = Stopwatch.StartNew();
        var result = await _manager.UninstallPackageAsync(package, options);
        watch.Stop();
        return new WinGetOperationResult(
            "uninstall",
            id,
            package.InstalledVersion.Version,
            result.Status.ToString(),
            result.ExtendedErrorCode is null ? null : Describe(result.ExtendedErrorCode),
            result.UninstallerErrorCode,
            result.RebootRequired,
            watch.Elapsed,
            logFile);
    }

    private static string? Metadata(PackageVersionInfo? version, PackageVersionMetadataField field) =>
        version?.GetMetadata(field) is { Length: > 0 } value ? value : null;

    internal static string Describe(Exception? error) =>
        error is null ? string.Empty : $"0x{error.HResult:X8} {error.Message}".Trim();

    private async Task<WinGetOperationResult> RunAsync(string operation, string id, string? version, PackageCatalogReference? source, bool acceptAgreements, string logFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFolder);
        Directory.CreateDirectory(logFolder);

        // Upgrade needs to know what is installed, install only the source; both find the package by its exact id.
        var package = await FindByIdAsync(Composite(source, operation == "upgrade" ? CompositeSearchBehavior.RemotePackagesFromAllCatalogs : CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs), id);

        var install = engine.CreateInstallOptions();
        install.PackageInstallScope = PackageInstallScope.System;
        install.PackageInstallMode = PackageInstallMode.Silent;
        install.AcceptPackageAgreements = acceptAgreements;
        var logFile = LogFile(logFolder, operation, id);
        install.LogOutputPath = logFile;
        if (version is not null)
        {
            install.PackageVersionId = package.AvailableVersions.FirstOrDefault(candidate => candidate.Version == version)
                ?? throw new WinGetException($"The source has no version {version} of {id}; it offers {string.Join(", ", package.AvailableVersions.Take(15).Select(candidate => candidate.Version))}.");
        }

        var watch = Stopwatch.StartNew();
        var result = operation == "upgrade"
            ? await _manager.UpgradePackageAsync(package, install)
            : await _manager.InstallPackageAsync(package, install);
        watch.Stop();
        return new WinGetOperationResult(
            operation,
            id,
            version ?? package.DefaultInstallVersion?.Version,
            result.Status.ToString(),
            result.ExtendedErrorCode is null ? null : Describe(result.ExtendedErrorCode),
            result.InstallerErrorCode,
            result.RebootRequired,
            watch.Elapsed,
            logFile);
    }

    private static string LogFile(string folder, string operation, string id) =>
        Path.Combine(folder, $"winget-{operation}-{Sanitize(id)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

    /// <summary>The package with exactly this id in the catalog.</summary>
    private async Task<CatalogPackage> FindByIdAsync(PackageCatalogReference reference, string id)
    {
        var catalog = await ConnectAsync(reference);
        var options = engine.CreateFindPackagesOptions();
        var filter = engine.CreatePackageMatchFilter();
        filter.Field = PackageMatchField.Id;
        filter.Option = PackageFieldMatchOption.Equals;
        filter.Value = id;
        options.Filters.Add(filter);
        return await FindAsync(catalog, options) is [var first, ..]
            ? first
            : throw new WinGetException($"No package with the id {id} in the source.");
    }

    private PackageCatalogReference Composite(PackageCatalogReference? source, CompositeSearchBehavior behavior)
    {
        var options = engine.CreateCompositeOptions();
        if (source is not null)
        {
            options.Catalogs.Add(source);
        }
        else
        {
            foreach (var catalog in _manager.GetPackageCatalogs())
            {
                options.Catalogs.Add(catalog);
            }
        }

        options.CompositeSearchBehavior = behavior;
        return _manager.CreateCompositePackageCatalog(options);
    }

    private static async Task<PackageCatalog> ConnectAsync(PackageCatalogReference reference)
    {
        var result = await reference.ConnectAsync();
        return result.Status == ConnectResultStatus.Ok
            ? result.PackageCatalog
            : throw new WinGetException($"Connecting to the source failed: {result.Status} {Describe(result.ExtendedErrorCode)}");
    }

    private static async Task<IReadOnlyList<CatalogPackage>> FindAsync(PackageCatalog catalog, FindPackagesOptions options)
    {
        var result = await catalog.FindPackagesAsync(options);
        return result.Status == FindPackagesResultStatus.Ok
            ? result.Matches.Select(match => match.CatalogPackage).ToList()
            : throw new WinGetException($"Searching failed: {result.Status} {Describe(result.ExtendedErrorCode)}");
    }

    private static string Sanitize(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
