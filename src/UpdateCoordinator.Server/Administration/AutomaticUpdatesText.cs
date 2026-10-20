using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Server.Administration;

/// <summary>Readable text for the Windows Automatic Updates mode an agent reports.</summary>
internal static class AutomaticUpdatesText
{
    public static string Describe(AutomaticUpdateMode? mode, bool byPolicy, bool managedByAgent = false)
    {
        if (mode is null)
        {
            return "reported by agents from version 0.3";
        }

        var text = mode switch
        {
            AutomaticUpdateMode.Disabled => "disabled",
            AutomaticUpdateMode.NotifyDownload => "notify before download",
            AutomaticUpdateMode.AutoDownloadNotifyInstall => "download, notify before install",
            AutomaticUpdateMode.ScheduledInstall => "installs on its own schedule",
            AutomaticUpdateMode.LocalAdminChooses => "local administrator chooses",
            AutomaticUpdateMode.NotifyInstallAndRestart => "notify before install and restart",
            _ => "not configured (Windows Server default: no automatic installation)",
        };
        return mode == AutomaticUpdateMode.NotConfigured ? text : $"{text} ({(managedByAgent ? "set by UpdateCoordinator" : byPolicy ? "group policy" : "local setting")})";
    }
}
