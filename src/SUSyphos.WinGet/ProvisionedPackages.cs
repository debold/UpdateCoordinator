using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using SUSyphos.Contracts.Software;

using Windows.Management.Deployment;

namespace SUSyphos.WinGet;

/// <summary>A packaged app (MSIX) provisioned for all users of the machine.</summary>
/// <param name="FamilyName">Package family name, such as Microsoft.PowerShell_8wekyb3d8bbwe.</param>
/// <param name="Name">Display name, or the package name when Windows gives none.</param>
/// <param name="Version">Package version (major.minor.build.revision).</param>
/// <param name="Publisher">Display name of the publisher.</param>
public sealed record ProvisionedPackage(string FamilyName, string Name, string Version, string? Publisher);

/// <summary>
/// Packaged apps provisioned for the machine. WinGet installs MSIX packages for the machine by provisioning them, but
/// under SYSTEM its installed list does not show provisioned packages (lab, Windows Server 2025), so the agent reads them
/// from Windows itself. The provisioned list exists from Windows 10.0.19041 on (Windows Server 2022).
/// </summary>
public static class ProvisionedPackages
{
    /// <summary>True where Windows has the provisioned list (PackageManager.FindProvisionedPackages).</summary>
    [SupportedOSPlatformGuard("windows10.0.19041")]
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>The provisioned apps without frameworks, resource packages and the packages of Windows itself.</summary>
    [SupportedOSPlatform("windows10.0.19041")]
    public static IReadOnlyList<ProvisionedPackage> Read()
    {
        var found = new List<ProvisionedPackage>();
        foreach (var package in new PackageManager().FindProvisionedPackages())
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System)
                {
                    continue;
                }

                var id = package.Id;
                var version = id.Version;
                var reported = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                found.Add(new ProvisionedPackage(
                    id.FamilyName,
                    Readable(() => package.DisplayName) ?? id.Name,
                    package.IsBundle ? ApplicationVersion(reported, Readable(() => package.InstalledLocation.Path), MsixManifest.MachineArchitecture()) : reported,
                    Readable(() => package.PublisherDisplayName)));
            }
            catch (COMException)
            {
                // A package Windows cannot describe is left out; the others are still worth reporting.
            }
        }

        return found;
    }

    /// <summary>
    /// The version of a provisioned bundle as WinGet and <c>Get-AppxPackage</c> show it: the version of its application
    /// package for this machine, read from the bundle manifest in its folder. A bundle carries its own version (lab,
    /// 2026-11-25: PowerShell 7.7 is provisioned as the bundle 2026.1110.418.0, its x64 package is 7.7.0.0). The bundle
    /// version stays when the manifest cannot be read or lists no fitting package.
    /// </summary>
    public static string ApplicationVersion(string bundleVersion, string? folder, string architecture)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return bundleVersion;
        }

        try
        {
            var path = Path.Combine(folder, MsixManifest.BundleManifestPath);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MsixManifest.MaxBytes)
            {
                return bundleVersion;
            }

            using var stream = file.OpenRead();
            return MsixManifest.Parse(stream)?.ApplicationVersion(architecture) ?? bundleVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return bundleVersion;
        }
    }

    /// <summary>
    /// The inventory with the provisioned apps added that it does not list yet (by package family name), the newest
    /// version once per family.
    /// </summary>
    public static IReadOnlyList<SoftwareItem> AddTo(IReadOnlyList<SoftwareItem> items, IReadOnlyList<ProvisionedPackage> provisioned)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(provisioned);
        var listed = items.Where(item => item.Source == SoftwareSource.Msix).Select(item => item.KeyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = provisioned
            .Where(package => !listed.Contains(package.FamilyName))
            .GroupBy(package => package.FamilyName, StringComparer.OrdinalIgnoreCase)
            .Select(family => family.MaxBy(package => System.Version.TryParse(package.Version, out var parsed) ? parsed : new Version(0, 0))!)
            .Select(package => new SoftwareItem(SoftwareSource.Msix, package.FamilyName, package.Name, package.Version, package.Publisher, null, null));
        return [.. items, .. added];
    }

    /// <summary>
    /// Removes a packaged app from the machine: the provisioning (so new users do not get it) and the installation of
    /// every user. Null when it worked or the app was not there, else why not.
    /// </summary>
    [SupportedOSPlatform("windows10.0.19041")]
    public static async Task<string?> RemoveAsync(string familyName, Action<string>? trace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        var manager = new PackageManager();

        // A family name comes from the manifest of a source: never one of the packages of Windows itself.
        if (manager.FindPackages(familyName).Concat(manager.FindProvisionedPackages()).Any(package => IsWindows(package) && string.Equals(package.Id.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{familyName} is part of Windows and is not removed.";
        }

        if (manager.FindProvisionedPackages().Any(package => string.Equals(package.Id.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)))
        {
            trace?.Invoke($"Removing the provisioning of {familyName}.");
            var result = await manager.DeprovisionPackageForAllUsersAsync(familyName);
            if (result.ExtendedErrorCode is { HResult: not 0 } error)
            {
                return $"The provisioning of {familyName} could not be removed: 0x{error.HResult:X8} {result.ErrorText}".Trim();
            }
        }

        foreach (var package in manager.FindPackages(familyName).ToList())
        {
            trace?.Invoke($"Removing {package.Id.FullName} for all users.");
            var result = await manager.RemovePackageAsync(package.Id.FullName, RemovalOptions.RemoveForAllUsers);
            if (result.ExtendedErrorCode is { HResult: not 0 } error)
            {
                return $"{package.Id.FullName} could not be removed: 0x{error.HResult:X8} {result.ErrorText}".Trim();
            }
        }

        return null;
    }

    /// <summary>True when the family is provisioned or installed for any user.</summary>
    [SupportedOSPlatform("windows10.0.19041")]
    public static bool IsPresent(string familyName)
    {
        var manager = new PackageManager();
        return manager.FindProvisionedPackages().Any(package => string.Equals(package.Id.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            || manager.FindPackages(familyName).Any();
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private static bool IsWindows(Windows.ApplicationModel.Package package)
    {
        try
        {
            return package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System || package.IsFramework;
        }
        catch (COMException)
        {
            return true;
        }
    }

    private static string? Readable(Func<string> read)
    {
        try
        {
            var text = read();
            return string.IsNullOrWhiteSpace(text) || text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : text.Trim();
        }
        catch (COMException)
        {
            return null;
        }
    }
}
