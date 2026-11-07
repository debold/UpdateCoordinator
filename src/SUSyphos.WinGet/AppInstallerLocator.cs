using System.Security.Principal;

namespace SUSyphos.WinGet;

/// <summary>One installed version of App Installer (Microsoft.DesktopAppInstaller), which brings WinGet.</summary>
/// <param name="FullName">Package full name.</param>
/// <param name="Version">Package version.</param>
/// <param name="Folder">Install folder under WindowsApps.</param>
/// <param name="RegisteredFor">Accounts the package is registered for, as names where Windows resolves the SID.</param>
public sealed record AppInstallerPackage(string FullName, Version Version, string Folder, IReadOnlyList<string> RegisteredFor)
{
    /// <summary>True when the folder holds the WinGet engine.</summary>
    public bool HasEngine => WinGetEngine.IsEngineFolder(Folder);
}

/// <summary>Finds App Installer on the machine for every user, which needs administrator rights (or SYSTEM).</summary>
public static class AppInstallerLocator
{
    public const string FamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    private static readonly string[] RuntimePackages = ["Microsoft.VCLibs.140.00.UWPDesktop", "Microsoft.VCLibs.140.00"];

    /// <summary>
    /// All installed versions, newest first: from the package manager of Windows, and from the WindowsApps folder when
    /// that fails (the API is missing on old builds or refuses the account).
    /// </summary>
    /// <param name="problems">Collects why a way of looking did not work, for the report.</param>
    public static IReadOnlyList<AppInstallerPackage> Find(ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            // For every user with administrator rights; without them only what the own account has registered.
            IEnumerable<Windows.ApplicationModel.Package> packages;
            try
            {
                packages = manager.FindPackages(FamilyName).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                problems.Add("Package manager of Windows: no administrator rights, only the packages of the own account are listed.");
                packages = manager.FindPackagesForUser(string.Empty, FamilyName).ToList();
            }

            var found = packages
                .Select(package =>
                {
                    var version = package.Id.Version;
                    var users = Safe(() => manager.FindUsers(package.Id.FullName).Select(user => AccountName(user.UserSecurityId)).ToList(), problems) ?? [];
                    return new AppInstallerPackage(package.Id.FullName, new Version(version.Major, version.Minor, version.Build, version.Revision), package.InstalledLocation.Path, users);
                })
                .OrderByDescending(package => package.Version)
                .ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }
        catch (Exception exception)
        {
            problems.Add($"Package manager of Windows: {exception.GetType().Name}: {exception.Message}");
        }

        return ScanWindowsApps(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), problems);
    }

    /// <summary>Looks for x64 App Installer folders by name, newest first.</summary>
    public static IReadOnlyList<AppInstallerPackage> ScanWindowsApps(string windowsApps, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(windowsApps);
        ArgumentNullException.ThrowIfNull(problems);
        try
        {
            return Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe")
                .Select(folder => (Folder: folder, Version: VersionFromFolder(Path.GetFileName(folder))))
                .Where(candidate => candidate.Version is not null)
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => new AppInstallerPackage(Path.GetFileName(candidate.Folder), candidate.Version!, candidate.Folder, []))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Folder {windowsApps}: {exception.GetType().Name}: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// The version from a folder name such as <c>Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe</c>; null
    /// for other architectures than x64 and for names that do not fit.
    /// </summary>
    public static Version? VersionFromFolder(string folderName, string packageName = "Microsoft.DesktopAppInstaller")
    {
        ArgumentNullException.ThrowIfNull(folderName);
        var parts = folderName.Split('_');
        return parts.Length == 5
            && parts[0].Equals(packageName, StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("x64", StringComparison.OrdinalIgnoreCase)
            && parts[4].Equals("8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(parts[1], out var version)
                ? version
                : null;
    }

    /// <summary>
    /// The newest x64 folder of each C++ runtime framework package (VCLibs) next to an App Installer folder: the engine
    /// of App Installer needs MSVCP140 and CONCRT140, which outside the package come from there unless the Visual C++
    /// redistributable is installed.
    /// </summary>
    public static IReadOnlyList<string> RuntimeFolders(string appInstallerFolder)
    {
        ArgumentNullException.ThrowIfNull(appInstallerFolder);
        var windowsApps = Path.GetDirectoryName(appInstallerFolder);
        if (windowsApps is null)
        {
            return [];
        }

        try
        {
            return RuntimePackages
                .Select(name => Directory.EnumerateDirectories(windowsApps, name + "_*_8wekyb3d8bbwe")
                    .Select(folder => (Folder: folder, Version: VersionFromFolder(Path.GetFileName(folder), name)))
                    .Where(candidate => candidate.Version is not null)
                    .OrderByDescending(candidate => candidate.Version)
                    .Select(candidate => candidate.Folder)
                    .FirstOrDefault())
                .OfType<string>()
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string AccountName(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception exception) when (exception is IdentityNotMappedException or SystemException)
        {
            return sid;
        }
    }

    private static T? Safe<T>(Func<T> read, ICollection<string> problems)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            problems.Add($"Users of App Installer: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }
}
