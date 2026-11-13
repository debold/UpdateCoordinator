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
/// <param name="ExtendedErrorCode">The HRESULT of <paramref name="ExtendedError"/>; 0 when WinGet gave none.</param>
public sealed record WinGetOperationResult(
    string Operation,
    string PackageId,
    string? Version,
    string Status,
    string? ExtendedError,
    uint InstallerErrorCode,
    bool RebootRequired,
    TimeSpan Duration,
    string LogFile,
    int ExtendedErrorCode = 0)
{
    public bool Succeeded => Status == nameof(InstallResultStatus.Ok);
}

/// <summary>Thrown when WinGet answers an operation with an error status before installing anything.</summary>
public sealed class WinGetException : Exception
{
    public WinGetException(string message)
        : base(message)
    {
    }

    /// <param name="message">What failed.</param>
    /// <param name="errorCode">The HRESULT WinGet gave, so callers can explain known codes.</param>
    public WinGetException(string message, int errorCode)
        : base(message) => HResult = errorCode;
}

/// <summary>
/// The operations the probe and later the agent need from WinGet: sources, search, installed list, install and upgrade
/// for the whole machine, silently. Agreements of a package are only accepted when the caller says so explicitly.
/// </summary>
public sealed class WinGetClient(WinGetEngine engine)
{
    /// <summary>Name under which a private REST source is added for <see cref="UseSourceAsync"/>.</summary>
    public const string PrivateSourceName = "susyphos";

    private readonly PackageManager _manager = engine.CreatePackageManager();

    /// <summary>Receives one line per step and progress state, so a hang shows where it happened.</summary>
    public Action<string>? Trace { get; init; }

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
            Trace?.Invoke($"Source {PrivateSourceName} is configured with {address}.");
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
        Trace?.Invoke($"Adding the source {PrivateSourceName} with {address}.");
        var result = await _manager.AddPackageCatalogAsync(options);
        if (result.Status != AddPackageCatalogStatus.Ok)
        {
            throw new WinGetException($"Adding the source failed: {result.Status} {Describe(result.ExtendedErrorCode)}", result.ExtendedErrorCode?.HResult ?? 0);
        }

        Trace?.Invoke("Source added.");

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
            logFile,
            result.ExtendedErrorCode?.HResult ?? 0);
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
        Trace?.Invoke($"Looking up {id} for {operation}.");
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

        // An upgrade keeps the architecture of the installed program: installers refuse to put x64 over x86 or the
        // other way round (lab, 2026-11-10: TeamViewer Host x86 ended with exit code 2).
        var keptArchitecture = false;
        if (operation == "upgrade" && InstalledArchitecture(package) is { } architecture)
        {
            install.AllowedArchitectures.Clear();
            install.AllowedArchitectures.Add(architecture);
            keptArchitecture = true;
            Trace?.Invoke($"Keeping the installed architecture {architecture}.");
        }

        Trace?.Invoke($"Starting the {operation} of {id} {install.PackageVersionId?.Version ?? package.DefaultInstallVersion?.Version}.");
        var watch = Stopwatch.StartNew();
        var result = await ExecuteAsync(operation, package, install);

        // The guess can be wrong: Burn bundles such as the Visual C++ Redistributable x64 register in the 32-bit view of
        // the registry although they are x64 (lab, 2026-11-13: 0x8A150010). Without an installer of that architecture
        // WinGet decides itself, as it would without the guess.
        if (keptArchitecture && result.Status == InstallResultStatus.NoApplicableInstallers)
        {
            install.AllowedArchitectures.Clear();
            Trace?.Invoke("No installer of the installed architecture; trying again with the architecture WinGet chooses.");
            result = await ExecuteAsync(operation, package, install);
        }

        watch.Stop();
        Trace?.Invoke($"WinGet answered {result.Status} after {watch.Elapsed.TotalSeconds:0.0} s (installer exit code {result.InstallerErrorCode}).");
        return new WinGetOperationResult(
            operation,
            id,
            version ?? package.DefaultInstallVersion?.Version,
            result.Status.ToString(),
            result.ExtendedErrorCode is null ? null : Describe(result.ExtendedErrorCode),
            result.InstallerErrorCode,
            result.RebootRequired,
            watch.Elapsed,
            logFile,
            result.ExtendedErrorCode?.HResult ?? 0);
    }

    private async Task<InstallResult> ExecuteAsync(string operation, CatalogPackage package, InstallOptions install)
    {
        var running = operation == "upgrade"
            ? _manager.UpgradePackageAsync(package, install)
            : _manager.InstallPackageAsync(package, install);
        var progress = new ProgressTrace(Trace);
        running.Progress = (_, state) => progress.Report(state);
        return await running;
    }

    /// <summary>
    /// The architecture of the installed program: x86 when its uninstall key is in the 32-bit view of the registry or it
    /// lives below "Program Files (x86)", x64 when it lives below "Program Files"; null when neither tells.
    /// </summary>
    internal static Windows.System.ProcessorArchitecture? InstalledArchitecture(CatalogPackage package)
    {
        var installed = package.InstalledVersion;
        if (installed is null)
        {
            return null;
        }

        if (installed.ProductCodes.Any(code => UninstallInventory.IsIn32BitViewOnly(code)))
        {
            return Windows.System.ProcessorArchitecture.X86;
        }

        return Architecture(Metadata(installed, PackageVersionMetadataField.InstalledLocation),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    }

    /// <summary>The architecture from the install location, given the 32-bit and 64-bit program folders.</summary>
    internal static Windows.System.ProcessorArchitecture? Architecture(string? location, string programFilesX86, string programFiles)
    {
        static bool Below(string path, string folder) =>
            folder.Length > 0 && path.StartsWith(folder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

        return location is null ? null
            : Below(location, programFilesX86) ? Windows.System.ProcessorArchitecture.X86
            : Below(location, programFiles) ? Windows.System.ProcessorArchitecture.X64
            : null;
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
            : throw new WinGetException($"Connecting to the source failed: {result.Status} {Describe(result.ExtendedErrorCode)}", result.ExtendedErrorCode?.HResult ?? 0);
    }

    private static async Task<IReadOnlyList<CatalogPackage>> FindAsync(PackageCatalog catalog, FindPackagesOptions options)
    {
        var result = await catalog.FindPackagesAsync(options);
        return result.Status == FindPackagesResultStatus.Ok
            ? result.Matches.Select(match => match.CatalogPackage).ToList()
            : throw new WinGetException($"Searching failed: {result.Status} {Describe(result.ExtendedErrorCode)}");
    }

    /// <summary>Turns WinGet's progress into a line per state and per quarter of the download.</summary>
    private sealed class ProgressTrace(Action<string>? trace)
    {
        private PackageInstallProgressState? _state;
        private int _quarter = -1;

        public void Report(InstallProgress progress)
        {
            if (trace is null)
            {
                return;
            }

            if (progress.State != _state)
            {
                _state = progress.State;
                trace($"Progress: {progress.State}.");
            }

            var quarter = progress.State == PackageInstallProgressState.Downloading ? (int)(progress.DownloadProgress * 4) : -1;
            if (quarter > _quarter)
            {
                _quarter = quarter;
                trace($"Downloaded {progress.BytesDownloaded:N0} of {progress.BytesRequired:N0} bytes.");
            }
        }
    }

    private static string Sanitize(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
