using System.Text.RegularExpressions;

using SUSyphos.Contracts.Agents;
using SUSyphos.WinGet;

namespace SUSyphos.Agent.WinGet;

/// <summary>
/// Installs or upgrades one package version for the machine, silently, from the WinGet source of the central server:
/// <c>install &lt;package id&gt; &lt;version&gt; &lt;source address&gt; [accept-agreements]</c>. Arguments are checked before WinGet sees them.
/// </summary>
internal static partial class InstallCommand
{
    public const string Name = "install";

    /// <summary>The flag that lets the installation accept the package's agreements (accepted by an Admin on the central server).</summary>
    public const string AcceptAgreements = "accept-agreements";

    /// <summary>The checked arguments, or null with the reason.</summary>
    /// <summary>Prefix of the optional argument naming the architecture of the installation to update ("arch=x64").</summary>
    public const string ArchitecturePrefix = "arch=";

    internal static (string PackageId, string Version, Uri Source, bool AcceptAgreements)? Parse(IReadOnlyList<string> arguments, out string? error) =>
        ParseWithArchitecture(arguments, out error) is { } parsed ? (parsed.PackageId, parsed.Version, parsed.Source, parsed.AcceptAgreements) : null;

    /// <summary>The checked arguments with the optional architecture (x64, x86 or arm64), or null with the reason.</summary>
    internal static (string PackageId, string Version, Uri Source, bool AcceptAgreements, string? Architecture)? ParseWithArchitecture(IReadOnlyList<string> arguments, out string? error)
    {
        error = null;
        var options = arguments.Skip(4).ToList();
        var architecture = options.FirstOrDefault(o => o.StartsWith(ArchitecturePrefix, StringComparison.Ordinal))?[ArchitecturePrefix.Length..];
        if (arguments is not [Name, var id, var version, var source, ..] || options.Count > 2 || options.Distinct().Count() != options.Count
            || options.Any(o => o != AcceptAgreements && !o.StartsWith(ArchitecturePrefix, StringComparison.Ordinal))
            || (architecture is not null && !SoftwareArchitecture.IsKnown(architecture)))
        {
            error = $"Usage: {Name} <package id> <version> <source address> [{AcceptAgreements}] [{ArchitecturePrefix}x64|x86|arm64]";
            return null;
        }

        if (id.Length > 128 || !PackageIdRegex().IsMatch(id))
        {
            error = "The package ID is invalid.";
            return null;
        }

        if (version.Length > 64 || !VersionRegex().IsMatch(version))
        {
            error = "The version is invalid.";
            return null;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.UserInfo))
        {
            error = "The source must be an https address.";
            return null;
        }

        return (id, version, address, options.Contains(AcceptAgreements), architecture);
    }

    /// <summary>Status of an outcome whose source check failed (the certificate, or the source not answering).</summary>
    public const string SourceUnavailable = "SourceUnavailable";

    public static async Task<SoftwareInstallOutcome> RunAsync(string packageId, string version, Uri source, bool acceptAgreements, string engineFolder, string logFolder, HelperTrace trace, Func<Uri, Task<(int HResult, string Detail)?>>? checkSource = null, string? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (SoftwareInventory.LocalAvailability() != WinGetAvailability.Available)
        {
            return new SoftwareInstallOutcome { Succeeded = false, Status = "NotSupported", Detail = "WinGet is not supported on this server." };
        }

        trace.Write($"Checking the WinGet source {source}.");
        if (await (checkSource ?? (address => SourceCheck.CheckAsync(address)))(source).ConfigureAwait(false) is { } problem)
        {
            trace.Write(problem.Detail);
            return new SoftwareInstallOutcome { Succeeded = false, Status = SourceUnavailable, Detail = Shorten(problem.Detail), HResult = problem.HResult };
        }

        try
        {
            trace.Write("Loading the WinGet engine.");
            using var engine = WinGetEngine.Load(engineFolder);
            var client = new WinGetClient(engine) { Trace = trace.Write };
            var catalog = await client.UseSourceAsync(source).ConfigureAwait(false);
            trace.Write("Reading the installed programs.");
            var installed = (await client.InstalledAsync().ConfigureAwait(false)).Any(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));
            var result = installed
                ? await client.UpgradeAsync(packageId, version, catalog, acceptAgreements, logFolder, WinGetClient.Parse(architecture)).ConfigureAwait(false)
                : await client.InstallAsync(packageId, version, catalog, acceptAgreements, logFolder, WinGetClient.Parse(architecture)).ConfigureAwait(false);
            return new SoftwareInstallOutcome
            {
                Succeeded = result.Succeeded,
                Status = result.Status,
                InstallerExitCode = result.InstallerErrorCode,
                RebootRequired = result.RebootRequired,
                Detail = result.Succeeded ? null : Shorten(result.ExtendedError ?? $"WinGet answered {result.Status}."),
                HResult = result.Succeeded ? 0 : result.ExtendedErrorCode,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            trace.Write($"WinGet failed: {exception}");
            return new SoftwareInstallOutcome { Succeeded = false, Status = "Error", Detail = Shorten($"WinGet failed: {exception.Message}"), HResult = exception is WinGetException ? exception.HResult : 0 };
        }
    }

    private static string Shorten(string text) => InventoryCommand.Text(text, 256) ?? "WinGet failed.";

    [GeneratedRegex(InstalledSoftware.PackageIdPattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex(InstalledSoftware.VersionPattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionRegex();
}
