using Microsoft.Win32;

namespace SUSyphos.WinGet;

/// <summary>Where an installed program was found.</summary>
public enum SoftwareSource
{
    Machine64,
    Machine32,
    Msix,
}

/// <summary>An installed program as the agent reports it: the uninstall entry, and the WinGet package WinGet correlated it with.</summary>
/// <param name="Source">Registry view or packaged app.</param>
/// <param name="KeyName">Uninstall key, or package family name of a packaged app.</param>
/// <param name="DisplayName">Name as "Programs and Features" shows it.</param>
/// <param name="DisplayVersion">Version as the program writes it.</param>
/// <param name="Publisher">Publisher as the program writes it.</param>
/// <param name="PackageId">The WinGet package, when WinGet found the program in a source.</param>
/// <param name="InstalledVersion">The installed version in WinGet's notation, when it found it.</param>
public sealed record SoftwareItem(
    SoftwareSource Source,
    string KeyName,
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? PackageId,
    string? InstalledVersion);

/// <summary>Whether WinGet can be used on this machine (Marc, 2026-11-08: from Windows Server 2019 with desktop).</summary>
public enum WinGetAvailability
{
    Available,
    TooOld,
    ServerCore,
}

/// <summary>Builds the software inventory from the uninstall keys and WinGet's installed list.</summary>
public static class SoftwareInventory
{
    /// <summary>Build of Windows Server 2019 / Windows 10 1809, the first where the WinGet engine runs (2016 lacks ICU).</summary>
    public const int MinimumBuild = 17763;

    private const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>
    /// Whether WinGet is supported: not before build 17763, and not on Server Core, where the COM API opens no catalog
    /// (lab, 2026-11-08). <paramref name="installationType"/> is the registry value InstallationType ("Server", "Server Core", "Client").
    /// </summary>
    public static WinGetAvailability Availability(int build, string? installationType) =>
        build < MinimumBuild ? WinGetAvailability.TooOld
        : string.Equals(installationType?.Trim(), "Server Core", StringComparison.OrdinalIgnoreCase) ? WinGetAvailability.ServerCore
        : WinGetAvailability.Available;

    /// <summary>The availability on the local machine.</summary>
    public static WinGetAvailability LocalAvailability()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionPath);
        return Availability(Environment.OSVersion.Version.Build, key?.GetValue("InstallationType") as string);
    }

    /// <summary>The uninstall entries alone, for machines without WinGet or when WinGet failed.</summary>
    public static IReadOnlyList<SoftwareItem> FromRegistry(IReadOnlyList<UninstallEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.Select(entry => Item(entry, null))];
    }

    /// <summary>
    /// Every uninstall entry, with the package where WinGet correlated the matching installation with a source, plus the
    /// packaged apps installed for the machine. Per-user programs are left out: SUSyphos manages the machine.
    /// </summary>
    public static IReadOnlyList<SoftwareItem> Build(IReadOnlyList<UninstallEntry> entries, IReadOnlyList<WinGetInstalledPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(packages);
        var comparison = InventoryMatcher.Compare(entries, packages);
        var matched = comparison.Matches.ToDictionary(match => match.Entry, match => match.Package);
        var items = entries.Select(entry => Item(entry, matched.GetValueOrDefault(entry))).ToList();
        items.AddRange(comparison.NotCompared
            .Where(package => package.Msix && !package.UserScope)
            .Select(package => new SoftwareItem(
                SoftwareSource.Msix,
                package.PackageFamilyNames is { Count: > 0 } names ? names[0] : package.Id,
                package.Name,
                package.InstalledVersion,
                null,
                package.Correlated ? package.Id : null,
                package.Correlated ? package.InstalledVersion : null)));
        return items;
    }

    private static SoftwareItem Item(UninstallEntry entry, WinGetInstalledPackage? package) => new(
        entry.View == UninstallView.Machine64 ? SoftwareSource.Machine64 : SoftwareSource.Machine32,
        entry.KeyName,
        entry.DisplayName,
        entry.DisplayVersion,
        entry.Publisher,
        package is { Correlated: true } ? package.Id : null,
        package is { Correlated: true } ? package.InstalledVersion : null);
}
