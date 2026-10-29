using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Inventory;

/// <summary>
/// Reads how Windows itself installs updates: group policy first, then the local setting
/// (see docs/05-agent-design.md, section 3, "Windows Automatic Updates"). Only reads; <see cref="AutomaticUpdatesManager"/> changes the setting when the central server asks for it.
/// </summary>
internal static class AutomaticUpdatesReader
{
    internal const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    internal const string PolicyAuPath = PolicyPath + @"\AU";
    internal const string LocalAuPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update";

    /// <summary>ProductType "WinNT" is Windows 10 or 11; servers report "ServerNT" or "LanmanNT" (domain controller).</summary>
    internal const string ProductOptionsPath = @"SYSTEM\CurrentControlSet\Control\ProductOptions";

    public static AutomaticUpdatesInfo Read(ISystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var scheduledRestart = probe.GetRegistryValue(PolicyAuPath, "AlwaysAutoRebootAtScheduledTime") == "1";
        var deadline = probe.GetRegistryValue(PolicyPath, "SetComplianceDeadline") == "1";
        var (mode, byPolicy) = ReadMode(probe);
        return new AutomaticUpdatesInfo { Mode = mode, ByPolicy = byPolicy, ScheduledRestart = scheduledRestart, Deadline = deadline, ManagedByAgent = AutomaticUpdatesManager.IsManagedByAgent(probe), ClientOperatingSystem = IsClientOperatingSystem(probe), UpdateForBusiness = HasUpdateForBusinessDeferrals(probe) };
    }

    /// <summary>"Select when Quality/Feature Updates are received" (Windows Update for Business): Windows withholds updates for the deferral days, also from the agent's search.</summary>
    private static bool HasUpdateForBusinessDeferrals(ISystemProbe probe) =>
        probe.GetRegistryValue(PolicyPath, "DeferQualityUpdates") == "1" || probe.GetRegistryValue(PolicyPath, "DeferFeatureUpdates") == "1";

    private static (AutomaticUpdateMode Mode, bool ByPolicy) ReadMode(ISystemProbe probe)
    {
        if (probe.GetRegistryValue(PolicyAuPath, "NoAutoUpdate") == "1")
        {
            return (AutomaticUpdateMode.Disabled, true);
        }

        // Option 5 lets the local administrator choose, so the local setting decides then.
        var policy = Parse(probe.GetRegistryValue(PolicyAuPath, "AUOptions"));
        if (policy is { } policyMode && policyMode != AutomaticUpdateMode.LocalAdminChooses)
        {
            return (policyMode, true);
        }

        var local = Parse(probe.GetRegistryValue(LocalAuPath, "AUOptions"));
        return local is { } localMode && localMode != AutomaticUpdateMode.LocalAdminChooses
            ? (localMode, false)
            : (policy ?? AutomaticUpdateMode.NotConfigured, policy is not null);
    }

    private static bool IsClientOperatingSystem(ISystemProbe probe) => string.Equals(probe.GetRegistryValue(ProductOptionsPath, "ProductType"), "WinNT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Known AUOptions values only; 1 means "keep my computer up to date" is off.</summary>
    private static AutomaticUpdateMode? Parse(string? value) => value switch
    {
        "1" => AutomaticUpdateMode.Disabled,
        "2" => AutomaticUpdateMode.NotifyDownload,
        "3" => AutomaticUpdateMode.AutoDownloadNotifyInstall,
        "4" => AutomaticUpdateMode.ScheduledInstall,
        "5" => AutomaticUpdateMode.LocalAdminChooses,
        "7" => AutomaticUpdateMode.NotifyInstallAndRestart,
        _ => null,
    };
}
