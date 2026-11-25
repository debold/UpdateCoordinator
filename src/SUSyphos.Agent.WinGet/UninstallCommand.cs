using System.Text.RegularExpressions;

using SUSyphos.Contracts.Agents;
using SUSyphos.WinGet;

namespace SUSyphos.Agent.WinGet;

/// <summary>Uninstalls an installed package for the machine, silently: <c>uninstall &lt;package id&gt;</c>.</summary>
internal static partial class UninstallCommand
{
    public const string Name = "uninstall";

    /// <summary>The checked package ID, or null with the reason.</summary>
    internal static string? Parse(IReadOnlyList<string> arguments, out string? error)
    {
        error = null;
        if (arguments is not [Name, var id])
        {
            error = $"Usage: {Name} <package id>";
            return null;
        }

        if (id.Length > 128 || !PackageIdRegex().IsMatch(id))
        {
            error = "The package ID is invalid.";
            return null;
        }

        return id;
    }

    public static async Task<SoftwareInstallOutcome> RunAsync(string packageId, string engineFolder, string logFolder, HelperTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (SoftwareInventory.LocalAvailability() != WinGetAvailability.Available)
        {
            return new SoftwareInstallOutcome { Succeeded = false, Status = "NotSupported", Detail = "WinGet is not supported on this server." };
        }

        try
        {
            trace.Write($"Uninstalling {packageId}.");
            using var engine = WinGetEngine.Load(engineFolder);
            HelperTrace.EnableVerboseWhenAsked(engine, trace);
            var client = new WinGetClient(engine) { Trace = trace.Write };
            if (!await client.IsInstalledAsync(packageId).ConfigureAwait(false) && await RemoveProvisionedAsync(client, packageId, trace).ConfigureAwait(false) is { } removed)
            {
                return removed;
            }

            var result = await client.UninstallAsync(packageId, null, logFolder).ConfigureAwait(false);
            trace.Write($"WinGet answered {result.Status} (uninstaller exit code {result.InstallerErrorCode}).");
            return new SoftwareInstallOutcome
            {
                Succeeded = result.Succeeded,
                Status = result.Status,
                InstallerExitCode = result.InstallerErrorCode,
                RebootRequired = result.RebootRequired,
                Detail = result.Succeeded ? null : InventoryCommand.Text(result.ExtendedError ?? $"WinGet answered {result.Status}.", 256),
                HResult = result.Succeeded ? 0 : result.ExtendedErrorCode,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            trace.Write($"WinGet failed: {exception}");
            return new SoftwareInstallOutcome { Succeeded = false, Status = exception is WinGetException ? "NotInstalled" : "Error", Detail = InventoryCommand.Text($"WinGet failed: {exception.Message}", 256) };
        }
    }

    /// <summary>
    /// A packaged app the machine has provisioned, which WinGet does not list under SYSTEM: removed for all users with
    /// Windows itself. Null when the package is no provisioned app here, so WinGet decides as before.
    /// </summary>
    private static async Task<SoftwareInstallOutcome?> RemoveProvisionedAsync(WinGetClient client, string packageId, HelperTrace trace)
    {
        if (!ProvisionedPackages.Supported)
        {
            return null;
        }

        IReadOnlyList<string> families;
        try
        {
            families = await client.PackageFamilyNamesAsync(packageId).ConfigureAwait(false);
        }
        catch (WinGetException exception)
        {
            trace.Write($"The package family names of {packageId} are unknown: {exception.Message}");
            return null;
        }

        var present = families.Where(ProvisionedPackages.IsPresent).ToList();
        if (present.Count == 0)
        {
            return null;
        }

        foreach (var family in present)
        {
            trace.Write($"{packageId} is the packaged app {family}; removing it for all users.");
            if (await ProvisionedPackages.RemoveAsync(family, trace.Write).ConfigureAwait(false) is { } error)
            {
                trace.Write(error);
                return new SoftwareInstallOutcome { Succeeded = false, Status = "Error", Detail = InventoryCommand.Text(error, 256) };
            }

            if (ProvisionedPackages.IsPresent(family))
            {
                return new SoftwareInstallOutcome { Succeeded = false, Status = "Error", Detail = InventoryCommand.Text($"{family} is still provisioned or installed after the removal.", 256) };
            }
        }

        trace.Write($"{packageId} was removed for all users.");
        return new SoftwareInstallOutcome { Succeeded = true, Status = "Ok" };
    }

    [GeneratedRegex(InstalledSoftware.PackageIdPattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdRegex();
}
