using System.Text.RegularExpressions;

using SUSyphos.Agent.Wua;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Inventory;

/// <summary>
/// Collects the inventory and shapes it to the contract limits, so a single odd value on the
/// server never blocks the whole inventory (the central server validates again).
/// </summary>
internal sealed partial class InventoryCollector(
    ISystemProbe probe,
    IInstalledUpdateSource updates,
    RoleDetector roles,
    IAgentEnvironment environment)
{
    private const string CurrentVersionPath = AgentEnvironment.CurrentVersionPath;
    private const string WindowsUpdatePolicyPath = AutomaticUpdatesReader.PolicyPath;

    public async Task<InventoryRequest> CollectAsync(CancellationToken cancellationToken)
    {
        var installed = await updates.GetInstalledUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var history = await updates.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        return new InventoryRequest
        {
            HostName = environment.HostName,
            DomainName = environment.DomainName,
            TimeZoneId = TimeZoneInfo.Local.Id,
            OperatingSystem = ReadOperatingSystem(),
            Roles = roles.Detect(),
            UpdateSource = ReadUpdateSource(probe),
            InstalledUpdates = installed is null ? null : WireFormat.ToInstalledUpdates(installed),
            UpdateHistory = history,
        };
    }

    private OperatingSystemInfo ReadOperatingSystem() => new()
    {
        ProductName = WireFormat.Text(probe.GetRegistryValue(CurrentVersionPath, "ProductName"), 128) ?? "Windows",
        EditionId = WireFormat.Match(probe.GetRegistryValue(CurrentVersionPath, "EditionID"), AlphanumericRegex(), 64),
        DisplayVersion = WireFormat.Match(probe.GetRegistryValue(CurrentVersionPath, "DisplayVersion"), VersionLabelRegex(), 16),
        Version = environment.OsVersion,
    };

    /// <summary>WSUS and Automatic Updates configuration; shared with the pre-flight check.</summary>
    internal static UpdateSourceInfo ReadUpdateSource(ISystemProbe probe)
    {
        var useWsus = probe.GetRegistryValue(WindowsUpdatePolicyPath + @"\AU", "UseWUServer") == "1";
        var server = WireFormat.Text(probe.GetRegistryValue(WindowsUpdatePolicyPath, "WUServer"), 255);
        return new UpdateSourceInfo { UsesWsus = useWsus && server is not null, WsusServer = useWsus ? server : null, AutomaticUpdates = AutomaticUpdatesReader.Read(probe) };
    }

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex AlphanumericRegex();

    [GeneratedRegex("^[A-Za-z0-9.]+$")]
    private static partial Regex VersionLabelRegex();
}
