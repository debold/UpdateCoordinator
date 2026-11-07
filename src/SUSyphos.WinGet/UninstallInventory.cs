using Microsoft.Win32;

namespace SUSyphos.WinGet;

/// <summary>Registry view an uninstall entry was read from.</summary>
public enum UninstallView
{
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall in the 64-bit view.</summary>
    Machine64,

    /// <summary>The same key in the 32-bit view (WOW6432Node).</summary>
    Machine32,
}

/// <summary>A program as "Programs and Features" lists it, from one key under Uninstall.</summary>
public sealed record UninstallEntry(
    UninstallView View,
    string KeyName,
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    bool WindowsInstaller);

/// <summary>
/// Reads the programs of the machine from the uninstall keys of the registry, without what "Programs and Features"
/// hides: system components, entries that belong to another (ParentKeyName), updates and entries without a name.
/// </summary>
public static class UninstallInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Reads both registry views of the local machine.</summary>
    public static IReadOnlyList<UninstallEntry> ReadMachine()
    {
        var entries = new List<UninstallEntry>();
        foreach (var (view, registryView) in new[] { (UninstallView.Machine64, RegistryView.Registry64), (UninstallView.Machine32, RegistryView.Registry32) })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
            using var uninstall = hive.OpenSubKey(UninstallPath);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key is null)
                    {
                        continue;
                    }

                    var values = key.GetValueNames().ToDictionary(name => name, name => key.GetValue(name), StringComparer.OrdinalIgnoreCase);
                    if (FromValues(view, keyName, values) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // A key the process may not read is skipped; it would not be in "Programs and Features" for it either.
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Builds an entry from the values of one uninstall key, or returns null when "Programs and Features" would not show it.
    /// </summary>
    public static UninstallEntry? FromValues(UninstallView view, string keyName, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        ArgumentNullException.ThrowIfNull(values);

        var name = Text(values, "DisplayName");
        if (name is null
            || Number(values, "SystemComponent") == 1
            || Text(values, "ParentKeyName") is not null
            || Text(values, "ReleaseType") is { } releaseType && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new UninstallEntry(view, keyName, name, Text(values, "DisplayVersion"), Text(values, "Publisher"), Number(values, "WindowsInstaller") == 1);
    }

    private static string? Text(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static long? Number(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var value)
            ? value switch
            {
                int number => number,
                long number => number,
                string text when long.TryParse(text, out var number) => number,
                _ => null,
            }
            : null;
}
