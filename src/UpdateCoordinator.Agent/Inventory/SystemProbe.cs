using Microsoft.Win32;

namespace UpdateCoordinator.Agent.Inventory;

/// <summary>
/// Read-only access to local system facts used by the inventory and the role rules.
/// Paths are relative to HKEY_LOCAL_MACHINE.
/// </summary>
internal interface ISystemProbe
{
    bool RegistryKeyExists(string path);

    /// <summary>Returns the value as string (numbers are converted invariantly), or null if it does not exist.</summary>
    string? GetRegistryValue(string path, string name);

    bool ServiceInstalled(string serviceName);
}

internal sealed class WindowsSystemProbe : ISystemProbe
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services\";

    public bool RegistryKeyExists(string path)
    {
        using var key = OpenKey(path);
        return key is not null;
    }

    public string? GetRegistryValue(string path, string name)
    {
        using var key = OpenKey(path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => null,
            string text => text,
            int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string[] lines => string.Join('\n', lines),
            _ => null,
        };
    }

    /// <summary>A service is installed if its key exists under Services; no service control access is needed.</summary>
    public bool ServiceInstalled(string serviceName) =>
        !serviceName.Contains('\\', StringComparison.Ordinal) && RegistryKeyExists(ServicesPath + serviceName);

    // Always the 64-bit view, so results do not depend on the process bitness.
    private static RegistryKey? OpenKey(string path)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        try
        {
            return root.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
