using System.Diagnostics;
using System.Text;

namespace SUSyphos.WinGet;

/// <summary>Files of an offline App Installer installation, as found in a folder.</summary>
/// <param name="Bundle">The App Installer bundle (Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle).</param>
/// <param name="Dependencies">x64 framework packages it needs (VCLibs, UI.Xaml, Windows App Runtime).</param>
/// <param name="License">License file of the release, if there is one (otherwise the package is added without).</param>
public sealed record OfflinePackageSet(string Bundle, IReadOnlyList<string> Dependencies, string? License);

/// <summary>Outcome of a PowerShell run.</summary>
public sealed record ScriptResult(int ExitCode, string Output, string Error, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Installs App Installer (and with it the WinGet engine) without anyone signing in: online through the PowerShell module
/// Microsoft.WinGet.Client, offline by provisioning the packages of a GitHub release from a folder. For the in-proc
/// engine it is enough that the files are on disk; nobody needs to have the app registered.
/// </summary>
public static class WinGetBootstrap
{
    /// <summary>Script that installs the module for all users and lets it repair WinGet for all users.</summary>
    public static string OnlineScript() =>
        """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
        Install-Module -Name Microsoft.WinGet.Client -Repository PSGallery -Scope AllUsers -Force -AllowClobber
        Import-Module Microsoft.WinGet.Client
        Repair-WinGetPackageManager -AllUsers -Force -Latest -Verbose
        """;

    /// <summary>Script that provisions the packages of <paramref name="set"/> for the machine.</summary>
    public static string OfflineScript(OfflinePackageSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.Append("Add-AppxProvisionedPackage -Online -PackagePath ").Append(Quote(set.Bundle));
        if (set.Dependencies.Count > 0)
        {
            script.Append(" -DependencyPackagePath @(").AppendJoin(", ", set.Dependencies.Select(Quote)).Append(')');
        }

        script.Append(set.License is null ? " -SkipLicense" : " -LicensePath " + Quote(set.License));
        script.AppendLine(" | Format-List | Out-String -Width 200");
        return script.ToString();
    }

    /// <summary>Finds the files of an offline installation in a folder and its subfolders.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidOperationException">No bundle or more than one.</exception>
    public static OfflinePackageSet FindOfflinePackages(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"The folder {folder} does not exist.");
        }

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        var bundles = files.Where(file => file.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(file).Contains("DesktopAppInstaller", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bundles.Count != 1)
        {
            throw new InvalidOperationException(bundles.Count == 0
                ? $"No App Installer bundle (Microsoft.DesktopAppInstaller_*.msixbundle) in {folder}."
                : $"More than one App Installer bundle in {folder}: {string.Join(", ", bundles.Select(Path.GetFileName))}.");
        }

        var dependencies = files
            .Where(file => (file.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)) && IsX64(folder, file))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var licenses = files.Where(file => file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(file).Contains("License", StringComparison.OrdinalIgnoreCase)).ToList();
        return new OfflinePackageSet(bundles[0], dependencies, licenses.Count == 1 ? licenses[0] : null);
    }

    /// <summary>Runs a script in Windows PowerShell without profile, passed encoded so nothing needs quoting on the command line.</summary>
    public static async Task<ScriptResult> RunAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(script) })
        {
            start.ArgumentList.Add(argument);
        }

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ScriptResult(process.ExitCode, await output, await error, watch.Elapsed);
    }

    /// <summary>A PowerShell string literal: single quotes, inner single quotes doubled, so nothing in it is evaluated.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // PowerShell treats the typographic quotes U+2018 to U+201B like the apostrophe.
        var quoted = new StringBuilder("'");
        foreach (var c in value)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                quoted.Append(c);
            }
        }

        return quoted.Append('\'').ToString();
    }

    /// <summary>The script as -EncodedCommand expects it: Base64 of UTF-16LE.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static bool IsX64(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return Path.GetFileName(file).Contains("_x64", StringComparison.OrdinalIgnoreCase)
            || relative.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(part => part.Equals("x64", StringComparison.OrdinalIgnoreCase));
    }
}
