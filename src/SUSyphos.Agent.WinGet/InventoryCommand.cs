using System.Text.RegularExpressions;

using SUSyphos.Contracts.Agents;
using SUSyphos.WinGet;

namespace SUSyphos.Agent.WinGet;

/// <summary>
/// The software inventory: the uninstall keys of the machine, matched with WinGet's installed list where WinGet is
/// supported. WinGet failing never loses the registry part.
/// </summary>
internal static partial class InventoryCommand
{
    public const string Name = "inventory";

    /// <summary>Folder of the bundled WinGet engine next to the helper.</summary>
    public const string EngineFolder = "winget-engine";

    public static async Task<SoftwareInventoryReport> RunAsync(string engineFolder, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var entries = UninstallInventory.ReadMachine();
        var availability = SoftwareInventory.LocalAvailability();
        if (availability != WinGetAvailability.Available)
        {
            return Report(SoftwareInventory.FromRegistry(entries), SoftwareSupportState.NotSupported, NotSupportedReason(availability));
        }

        try
        {
            using var engine = WinGetEngine.Load(engineFolder);
            var installed = await new WinGetClient(engine).InstalledAsync().ConfigureAwait(false);
            return Report(SoftwareInventory.Build(entries, installed), SoftwareSupportState.Supported, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The registry part is still worth reporting; the reason goes to the dashboard, the details to the agent log.
            await log.WriteLineAsync($"WinGet failed: {exception}").ConfigureAwait(false);
            return Report(SoftwareInventory.FromRegistry(entries), SoftwareSupportState.Failed, $"WinGet failed: {exception.Message}");
        }
    }

    internal static string NotSupportedReason(WinGetAvailability availability) => availability == WinGetAvailability.ServerCore
        ? "Server Core: WinGet needs the desktop experience."
        : "WinGet needs Windows Server 2019 or later.";

    /// <summary>Shapes the inventory to the limits of the contract, so a single odd entry never fails the whole inventory.</summary>
    internal static SoftwareInventoryReport Report(IReadOnlyList<SoftwareItem> items, SoftwareSupportState state, string? detail) => new()
    {
        Software = [.. items.Select(ToContract).OfType<InstalledSoftware>().Take(InstalledSoftware.MaxEntries)],
        Support = new SoftwareSupport { State = state, Detail = Text(detail, 256) },
    };

    internal static InstalledSoftware? ToContract(SoftwareItem item)
    {
        var keyName = Text(item.KeyName, 256);
        var displayName = Text(item.DisplayName, 256);
        if (keyName is null || displayName is null)
        {
            return null;
        }

        var packageId = item.PackageId is { Length: <= 128 } id && PackageIdRegex().IsMatch(id) ? id : null;
        return new InstalledSoftware
        {
            View = item.Source switch { SoftwareSource.Machine32 => SoftwareView.Machine32, SoftwareSource.Msix => SoftwareView.Msix, _ => SoftwareView.Machine64 },
            KeyName = keyName,
            DisplayName = displayName,
            DisplayVersion = Version(item.DisplayVersion),
            Publisher = Text(item.Publisher, 256),
            PackageId = packageId,
            InstalledVersion = packageId is null ? null : Version(item.InstalledVersion),
        };
    }

    /// <summary>Text without control characters, trimmed and cut to the length; null when nothing is left.</summary>
    internal static string? Text(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = new string([.. value.Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? Version(string? value) => Text(value, 64) is { } text && VersionRegex().IsMatch(text) ? text : null;

    [GeneratedRegex(InstalledSoftware.PackageIdPattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex(InstalledSoftware.VersionPattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionRegex();
}
