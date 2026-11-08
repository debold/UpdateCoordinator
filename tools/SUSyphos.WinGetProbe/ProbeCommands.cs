using SUSyphos.WinGet;

namespace SUSyphos.WinGetProbe;

/// <summary>What the report says about the engine: which one was asked for, where it was looked for and whether it loaded.</summary>
public sealed class EngineReport(EngineChoice choice)
{
    public EngineChoice Choice { get; } = choice;

    public IReadOnlyList<AppInstallerPackage> AppInstaller { get; set; } = [];

    public List<string> Problems { get; } = [];

    public string? Folder { get; set; }

    public IReadOnlyList<string> DependencyFolders { get; set; } = [];

    public bool Loaded { get; set; }

    /// <summary>System libraries the engine imports that Windows could not load (after error 126).</summary>
    public IReadOnlyList<string>? MissingLibraries { get; set; }

    public ProbeError? LoadError { get; set; }
}

/// <summary>The commands of the probe; each returns the data part of its report.</summary>
public static class ProbeCommands
{
    public static async Task<object?> RunAsync(ProbeArguments arguments, MachineInfo machine, EngineReport engine, ProbeOutput output)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(output);

        engine.AppInstaller = AppInstallerLocator.Find(engine.Problems);
        output.Log(engine.AppInstaller.Count == 0
            ? "App Installer: not installed"
            : "App Installer: " + string.Join("; ", engine.AppInstaller.Select(package => $"{package.Version} engine {package.HasEngine}, registered for {(package.RegisteredFor.Count == 0 ? "nobody known" : string.Join(", ", package.RegisteredFor))}")));

        switch (arguments.Command)
        {
            case "bootstrap":
                return await BootstrapAsync(arguments, machine, engine, output);
            case "info":
                return await InfoAsync(arguments, engine, output);
            case "serve":
                return await ProbeServe.ServeAsync(arguments, output);
            case "cache":
                // The engine is only needed to find the newest version.
                return await ProbeServe.CacheAsync(arguments, () => new WinGetClient(Load(arguments.Engine, engine, output)), output);
        }

        var client = new WinGetClient(Load(arguments.Engine, engine, output));
        var source = arguments.Source is null ? null : await client.UseSourceAsync(arguments.Source);
        switch (arguments.Command)
        {
            case "inventory":
                return await InventoryAsync(client, output);
            case "search":
                var found = await client.SearchAsync(arguments.Target!, source);
                output.Log($"Search '{arguments.Target}': {found.Count} packages");
                return found;
            default:
                output.Log($"{arguments.Command} {arguments.Target} {arguments.Version ?? "(newest)"} from {arguments.Source?.ToString() ?? "winget"}, agreements accepted: {arguments.AcceptAgreements}");
                var result = arguments.Command == "upgrade"
                    ? await client.UpgradeAsync(arguments.Target!, arguments.Version, source, arguments.AcceptAgreements, output.WinGetLogFolder)
                    : await client.InstallAsync(arguments.Target!, arguments.Version, source, arguments.AcceptAgreements, output.WinGetLogFolder);
                output.Log($"Result {result.Status}, installer exit code {result.InstallerErrorCode}, reboot required {result.RebootRequired}, {result.Duration.TotalSeconds:0.0} s {result.ExtendedError}");
                if (!result.Succeeded)
                {
                    throw new ProbeFailedException($"{arguments.Command} ended with {result.Status} (installer exit code {result.InstallerErrorCode}) {result.ExtendedError}".Trim(), result);
                }

                return result;
        }
    }

    private static WinGetEngine Load(EngineChoice choice, EngineReport report, ProbeOutput output)
    {
        try
        {
            report.Folder = choice == EngineChoice.Bundled
                ? Path.Combine(AppContext.BaseDirectory, "engine")
                : report.AppInstaller.FirstOrDefault(package => package.HasEngine)?.Folder
                    ?? throw new WinGetException(report.AppInstaller.Count == 0
                        ? "App Installer is not installed (bootstrap, or --engine bundled)."
                        : "No installed App Installer holds the engine (" + WinGetEngine.EngineFileName + ").");
            var dependencies = choice == EngineChoice.Installed ? AppInstallerLocator.RuntimeFolders(report.Folder) : [];
            report.DependencyFolders = dependencies;
            var engine = WinGetEngine.Load(report.Folder, dependencies);
            report.Loaded = true;
            output.Log($"Engine loaded: {engine.File}" + (dependencies.Count == 0 ? string.Empty : $" (runtime from {string.Join(", ", dependencies)})"));
            return engine;
        }
        catch (Exception exception)
        {
            report.LoadError = ProbeError.From(exception);
            if (exception is System.ComponentModel.Win32Exception { NativeErrorCode: 126 } && report.Folder is not null)
            {
                // "Module not found": name the system libraries the engine needs that this Windows does not have.
                report.MissingLibraries = DependencyCheck.MissingSystemLibraries(report.Folder);
                output.Log("Missing system libraries: " + (report.MissingLibraries.Count == 0 ? "none found" : string.Join(", ", report.MissingLibraries)));
            }

            throw;
        }
    }

    private static async Task<object> InfoAsync(ProbeArguments arguments, EngineReport engine, ProbeOutput output)
    {
        try
        {
            var client = new WinGetClient(Load(arguments.Engine, engine, output));
            var sources = client.Sources();
            output.Log("Sources: " + string.Join(", ", sources.Select(source => $"{source.Name} ({source.Type})")));
            return await Task.FromResult(new { Sources = sources });
        }
        catch (Exception exception)
        {
            // info reports what it found even when WinGet is not usable; that is a result, not a failure of the probe.
            output.Log($"WinGet not usable: {exception.Message}");
            return new { Sources = (object?)null, WinGetError = ProbeError.From(exception) };
        }
    }

    private static async Task<object> InventoryAsync(WinGetClient client, ProbeOutput output)
    {
        var entries = UninstallInventory.ReadMachine();
        var installed = await client.InstalledAsync();
        var comparison = InventoryMatcher.Compare(entries, installed);
        var summary = new
        {
            UninstallEntries = entries.Count,
            WinGetInstalled = installed.Count,
            WinGetNotCompared = comparison.NotCompared.Count,
            WinGetCorrelated = installed.Count(package => package.Correlated),
            Matched = comparison.Matches.Count,
            MatchedByKind = comparison.Matches.GroupBy(match => match.Kind).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            Updatable = comparison.Updatable,
            UpdateAvailable = comparison.Matches.Count(match => match.Package.UpdateAvailable),
        };
        output.Log($"Inventory: {summary.UninstallEntries} uninstall entries, {summary.WinGetInstalled} in WinGet ({summary.WinGetNotCompared} MSIX or per user), matched {summary.Matched}, WinGet could update {summary.Updatable}");
        return new
        {
            Summary = summary,
            Matches = comparison.Matches.Select(match => new
            {
                match.Kind,
                match.Entry.DisplayName,
                match.Entry.DisplayVersion,
                match.Entry.View,
                match.Package.Id,
                match.Package.AvailableVersion,
                match.Package.UpdateAvailable,
                match.Package.Source,
            }),
            comparison.OnlyInRegistry,
            comparison.OnlyInWinGet,
            NotCompared = comparison.NotCompared.Select(package => new { package.Id, package.Name, package.InstalledVersion, package.Scope, Msix = package.Msix }),
        };
    }

    private static async Task<object> BootstrapAsync(ProbeArguments arguments, MachineInfo machine, EngineReport engine, ProbeOutput output)
    {
        if (machine.BelowAppInstallerMinimum)
        {
            output.Log($"Build {machine.Build} is below 17763: App Installer is expected to refuse; trying anyway to record the error.");
        }

        var before = engine.AppInstaller;
        OfflinePackageSet? set = null;
        string script;
        if (arguments.Online)
        {
            script = WinGetBootstrap.OnlineScript();
        }
        else
        {
            set = WinGetBootstrap.FindOfflinePackages(arguments.OfflineFolder!);
            output.Log($"Offline set: {Path.GetFileName(set.Bundle)}, {set.Dependencies.Count} dependencies, license {(set.License is null ? "none (-SkipLicense)" : Path.GetFileName(set.License))}");
            script = WinGetBootstrap.OfflineScript(set);
        }

        output.Log($"Running PowerShell ({(arguments.Online ? "online" : "offline")})");
        var result = await WinGetBootstrap.RunAsync(script);
        output.Log($"PowerShell exit code {result.ExitCode} after {result.Duration.TotalSeconds:0.0} s");
        var after = AppInstallerLocator.Find(engine.Problems);
        engine.AppInstaller = after;
        if (!result.Succeeded)
        {
            output.Log(result.Error.Trim());
        }

        var report = new { Mode = arguments.Online ? "online" : "offline", Set = set, Script = script, Result = result, Before = before, After = after };
        return result.Succeeded
            ? report
            : throw new ProbeFailedException($"PowerShell ended with exit code {result.ExitCode}: {FirstLine(result.Error)}", report);
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
}

/// <summary>A command that ran but failed; the report still carries what it found.</summary>
public sealed class ProbeFailedException(string message, object details) : Exception(message)
{
    public object Details { get; } = details;
}
