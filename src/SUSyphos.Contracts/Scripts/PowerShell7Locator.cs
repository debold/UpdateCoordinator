using System.Globalization;
using System.Text.RegularExpressions;

namespace SUSyphos.Contracts.Scripts;

/// <summary>
/// Finds pwsh.exe of PowerShell 7 on agent and central server alike. PowerShell 7.7 and later come only as an MSIX
/// package, which WinGet provisions for the machine below <c>Program Files\WindowsApps</c>; older versions and the LTS MSI
/// install to <c>Program Files\PowerShell\7</c>. Both folders are writable only by administrators and the system; pwsh is
/// never taken from the PATH.
/// </summary>
public static partial class PowerShell7Locator
{
    /// <summary>
    /// pwsh.exe under <paramref name="programFiles"/>: the MSI folder first, else the newest x64 package of the family
    /// Microsoft.PowerShell_8wekyb3d8bbwe (not the preview); null when neither has it.
    /// </summary>
    public static string? Find(string programFiles, Func<string, bool>? exists = null, Func<string, IEnumerable<string>>? folders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programFiles);
        exists ??= File.Exists;
        folders ??= SafeFolders;

        var installed = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (exists(installed))
        {
            return installed;
        }

        return folders(Path.Combine(programFiles, "WindowsApps"))
            .Select(folder => (Folder: folder, Match: PackageFolderRegex().Match(Path.GetFileName(folder))))
            .Where(candidate => candidate.Match.Success && Version.TryParse(candidate.Match.Groups["version"].Value, out _))
            .OrderByDescending(candidate => Version.Parse(candidate.Match.Groups["version"].Value))
            .Select(candidate => Path.Combine(candidate.Folder, "pwsh.exe"))
            .FirstOrDefault(exists);
    }

    /// <summary>pwsh.exe on this machine, from the Program Files folder of the 64-bit system.</summary>
    public static string? FindLocal() =>
        Find(Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } x64 ? x64 : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    private static string[] SafeFolders(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path, "Microsoft.PowerShell_*") : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Folder of the x64 package, such as Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe.</summary>
    [GeneratedRegex(@"^Microsoft\.PowerShell_(?<version>\d{1,5}(?:\.\d{1,5}){1,3})_x64__8wekyb3d8bbwe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PackageFolderRegex();

    /// <summary>For logs and messages: the version of the file, "7.7.0", or null.</summary>
    public static string? FileVersion(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart > 0 ? string.Create(CultureInfo.InvariantCulture, $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}") : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
