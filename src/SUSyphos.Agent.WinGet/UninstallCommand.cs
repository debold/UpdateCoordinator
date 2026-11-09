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

    public static async Task<SoftwareInstallOutcome> RunAsync(string packageId, string engineFolder, string logFolder, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (SoftwareInventory.LocalAvailability() != WinGetAvailability.Available)
        {
            return new SoftwareInstallOutcome { Succeeded = false, Status = "NotSupported", Detail = "WinGet is not supported on this server." };
        }

        try
        {
            using var engine = WinGetEngine.Load(engineFolder);
            var result = await new WinGetClient(engine).UninstallAsync(packageId, null, logFolder).ConfigureAwait(false);
            return new SoftwareInstallOutcome
            {
                Succeeded = result.Succeeded,
                Status = result.Status,
                InstallerExitCode = result.InstallerErrorCode,
                RebootRequired = result.RebootRequired,
                Detail = result.Succeeded ? null : InventoryCommand.Text(result.ExtendedError ?? $"WinGet answered {result.Status}.", 256),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await log.WriteLineAsync($"WinGet failed: {exception}").ConfigureAwait(false);
            return new SoftwareInstallOutcome { Succeeded = false, Status = exception is WinGetException ? "NotInstalled" : "Error", Detail = InventoryCommand.Text($"WinGet failed: {exception.Message}", 256) };
        }
    }

    [GeneratedRegex(InstalledSoftware.PackageIdPattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdRegex();
}
