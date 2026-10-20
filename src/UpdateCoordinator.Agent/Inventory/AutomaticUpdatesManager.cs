using Microsoft.Win32;

using UpdateCoordinator.Agent.Configuration;

namespace UpdateCoordinator.Agent.Inventory;

/// <summary>Writes registry values under HKEY_LOCAL_MACHINE; replaced by a fake in tests, so no test changes the machine.</summary>
internal interface IRegistryWriter
{
    void SetDword(string path, string name, int value);

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

    public void DeleteValue(string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Keeps Windows from installing updates on its own when the central server asks for it (opt-in, decided by Marc on
/// 2026-10-20; docs/05-agent-design.md, section 3): sets "Configure Automatic Updates" to option 3 (download, notify before
/// install) in the policy registry key, the way sconfig does, but only where no group policy controls the setting. A marker
/// in the agent key records the value the agent wrote, so it only ever restores its own change and never touches a value
/// set by group policy.
/// </summary>
internal sealed partial class AutomaticUpdatesManager(ISystemProbe probe, IRegistryWriter registry, ILogger<AutomaticUpdatesManager> logger)
{
    /// <summary>Option 3: download automatically, notify before installation.</summary>
    internal const int NotifyBeforeInstall = 3;

    internal const string MarkerName = "ManagedAutomaticUpdates";

    private bool _policyLogged;

    /// <summary>True if the policy value is the one the agent wrote.</summary>
    public static bool IsManagedByAgent(ISystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var marker = probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MarkerName);
        return marker == "3"
            && probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions") == marker
            && probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate") != "1";
    }

    /// <summary>Applies the central setting; cheap to call every cycle, it only writes when the registry differs.</summary>
    public void Apply(bool manage)
    {
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

            registry.SetDword(AutomaticUpdatesReader.PolicyAuPath, "AUOptions", NotifyBeforeInstall);
            registry.SetDword(AgentRegistry.InstallerKeyPath, MarkerName, NotifyBeforeInstall);
            LogApplied();
        }
        else if (probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MarkerName) is not null)
        {
            // Only the agent's own value is removed; if group policy replaced it meanwhile, just the marker goes.
            if (ours)
            {
                registry.DeleteValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions");
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows Automatic Updates set to option 3 (download, notify before install) as configured on the central server.")]
    private partial void LogApplied();

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows Automatic Updates restored: the agent removed the value it had set.")]
    private partial void LogRestored();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows Automatic Updates are controlled by group policy; the agent leaves them unchanged.")]
    private partial void LogControlledByPolicy();
}
