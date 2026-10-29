using Microsoft.Win32;

using SUSyphos.Agent.Configuration;

namespace SUSyphos.Agent.Inventory;

/// <summary>Writes registry values under HKEY_LOCAL_MACHINE; replaced by a fake in tests, so no test changes the machine.</summary>
internal interface IRegistryWriter
{
    void SetDword(string path, string name, int value);

    void SetString(string path, string name, string value);

    void DeleteValue(string path, string name);
}

internal sealed class WindowsRegistryWriter : IRegistryWriter
{
    public void SetDword(string path, string name, int value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(path, writable: true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void SetString(string path, string name, string value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(path, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Keeps Windows from installing updates on its own when the central server asks for it (opt-in, decided by Marc on
/// 2026-10-20; docs/05-agent-design.md, section 3): turns Windows Automatic Updates off (NoAutoUpdate = 1 in the policy
/// registry key, as Azure Update Manager does for manual patching; Marc, 2026-10-29), but only where no group policy controls
/// the setting. The agent still searches and installs through the Windows Update API. Option 3 (download, notify), used
/// before, let Windows download the update and offer it for installation. A marker in the agent key records the value the
/// agent wrote, so it only ever removes its own change and never touches a value set by group policy.
/// </summary>
internal sealed partial class AutomaticUpdatesManager(ISystemProbe probe, IRegistryWriter registry, ILogger<AutomaticUpdatesManager> logger)
{
    internal const string MarkerName = "ManagedAutomaticUpdates";

    /// <summary>Marker value: the agent wrote NoAutoUpdate = 1.</summary>
    internal const string NoAutoUpdateMarker = "NoAutoUpdate";

    /// <summary>Marker value of agents up to 0.7.13, which wrote AUOptions = 3; replaced on the next cycle.</summary>
    internal const string OptionThreeMarker = "3";

    private bool _policyLogged;

    /// <summary>True if the policy value is the one the agent wrote.</summary>
    public static bool IsManagedByAgent(ISystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MarkerName) == NoAutoUpdateMarker
            && probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate") == "1";
    }

    /// <summary>True if the policy holds the option 3 an earlier agent version wrote.</summary>
    private bool HasOwnOptionThree() =>
        probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MarkerName) == OptionThreeMarker
        && probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions") == OptionThreeMarker;

    /// <summary>Applies the central setting; cheap to call every cycle, it only writes when the registry differs.</summary>
    public void Apply(bool manage)
    {
        // The option 3 of an earlier version goes first; switched on, NoAutoUpdate takes its place below.
        if (HasOwnOptionThree())
        {
            registry.DeleteValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions");
            registry.DeleteValue(AgentRegistry.InstallerKeyPath, MarkerName);
        }

        var ours = IsManagedByAgent(probe);
        if (manage)
        {
            if (ours)
            {
                return;
            }

            if (IsControlledByPolicy())
            {
                if (!_policyLogged)
                {
                    LogControlledByPolicy();
                    _policyLogged = true;
                }

                return;
            }

            registry.SetDword(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate", 1);
            registry.SetString(AgentRegistry.InstallerKeyPath, MarkerName, NoAutoUpdateMarker);
            LogApplied();
        }
        else if (probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MarkerName) is not null)
        {
            // Only the agent's own value is removed; if group policy replaced it meanwhile, just the marker goes.
            if (ours)
            {
                registry.DeleteValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate");
            }

            registry.DeleteValue(AgentRegistry.InstallerKeyPath, MarkerName);
            LogRestored();
        }

        _policyLogged = false;
    }

    /// <summary>A value the agent did not write: group policy (or an administrator) decides, the agent stays out.</summary>
    private bool IsControlledByPolicy()
    {
        var options = probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions");
        return probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate") == "1"
            || (options is not null && options != "5");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows Automatic Updates turned off (NoAutoUpdate = 1) as configured on the central server; the agent installs through the Windows Update API.")]
    private partial void LogApplied();

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows Automatic Updates restored: the agent removed the value it had set.")]
    private partial void LogRestored();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows Automatic Updates are controlled by group policy; the agent leaves them unchanged.")]
    private partial void LogControlledByPolicy();
}
