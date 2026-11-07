using System.Security.Principal;

using Microsoft.Win32;

namespace SUSyphos.WinGet;

/// <summary>What the probe reports about the machine and the account it runs as.</summary>
public sealed record MachineInfo(
    string ComputerName,
    string? ProductName,
    string? DisplayVersion,
    int? Build,
    int? Revision,
    string? InstallationType,
    bool ServerCore,
    string Account,
    bool System,
    bool Administrator,
    bool Is64BitProcess)
{
    /// <summary>Windows Server 2016 (build 14393) and older builds, where App Installer cannot be installed (it needs 17763).</summary>
    public bool BelowAppInstallerMinimum => Build is < 17763;

    public static MachineInfo Collect()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var installationType = key?.GetValue("InstallationType") as string;
        using var identity = WindowsIdentity.GetCurrent();
        return new MachineInfo(
            Environment.MachineName,
            key?.GetValue("ProductName") as string,
            key?.GetValue("DisplayVersion") as string,
            int.TryParse(key?.GetValue("CurrentBuild") as string, out var build) ? build : null,
            key?.GetValue("UBR") is int revision ? revision : null,
            installationType,
            string.Equals(installationType, "Server Core", StringComparison.OrdinalIgnoreCase),
            identity.Name,
            identity.IsSystem,
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            Environment.Is64BitProcess);
    }
}
