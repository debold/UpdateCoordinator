using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SUSyphos.Agent.Installation;

internal enum UninstallStatus
{
    Succeeded,
    NotInstalled,
    NotUninstallable,
    Failed,
}

/// <summary>Outcome of an uninstallation: how it was done, whether Windows needs a restart, and a line for the dashboard.</summary>
internal sealed record UninstallResult(UninstallStatus Status, bool RebootRequired, string Detail);

/// <summary>Removes an installed update (remote action, Marc 2026-10-22): through Windows Update, else with DISM.</summary>
internal interface IUpdateUninstaller
{
    Task<UninstallResult> UninstallAsync(Guid updateId, string? kbArticle, CancellationToken cancellationToken);
}

/// <summary>Removes an update through the Windows Update Agent API; returns null if WUA cannot (not found or not uninstallable).</summary>
internal interface IWuaUninstall
{
    UninstallResult? TryUninstall(Guid updateId);
}

/// <summary>Removes a package with DISM by its KB number.</summary>
internal interface IDismUninstall
{
    UninstallResult Uninstall(string kbArticle);
}

/// <summary>Windows Update first; for updates it cannot remove (e.g. combined servicing stack and cumulative packages) DISM by KB number.</summary>
internal sealed partial class UpdateUninstaller(IWuaUninstall wua, IDismUninstall dism, ILogger<UpdateUninstaller> logger) : IUpdateUninstaller
{
    public Task<UninstallResult> UninstallAsync(Guid updateId, string? kbArticle, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var viaWua = wua.TryUninstall(updateId);
                if (viaWua is not null)
                {
                    return viaWua;
                }

                if (kbArticle is null || !KbRegex().IsMatch(kbArticle))
                {
                    return new UninstallResult(UninstallStatus.NotUninstallable, false, "Windows Update cannot uninstall the update, and without a KB number DISM cannot find its package.");
                }

                LogDismFallback(updateId, kbArticle);
                return dism.Uninstall(kbArticle);
            },
            cancellationToken);

    [GeneratedRegex("^[0-9]{5,8}$", RegexOptions.CultureInvariant)]
    internal static partial Regex KbRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows Update cannot uninstall {UpdateId}; trying DISM with KB{Kb}.")]
    private partial void LogDismFallback(Guid updateId, string kb);
}

internal sealed class WuaUninstall : IWuaUninstall
{
    private const string SessionProgId = "Microsoft.Update.Session";
    private const string CollectionProgId = "Microsoft.Update.UpdateColl";

    public UninstallResult? TryUninstall(Guid updateId)
    {
        using var com = new Wua.ComScope();
        try
        {
            dynamic session = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID(SessionProgId, throwOnError: true)!)!);
            session.ClientApplicationID = "SUSyphos.Agent";
            dynamic searcher = com.Track(session.CreateUpdateSearcher());
            searcher.Online = false;
            dynamic found = com.Track(com.Track(searcher.Search(string.Create(CultureInfo.InvariantCulture, $"UpdateID='{updateId:D}' and IsInstalled=1"))).Updates);
            if ((int)found.Count == 0)
            {
                return null;
            }

            dynamic update = com.Track(found.Item(0));
            if (!(bool)update.IsUninstallable)
            {
                return null;
            }

            dynamic collection = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID(CollectionProgId, throwOnError: true)!)!);
            collection.Add(update);
            dynamic installer = com.Track(session.CreateUpdateInstaller());
            installer.Updates = collection;
            dynamic result = com.Track(installer.Uninstall());
            var code = (int)result.ResultCode;
            var reboot = (bool)result.RebootRequired;

            // 2 = succeeded, 3 = succeeded with errors (OperationResultCode).
            return code is 2 or 3
                ? new UninstallResult(UninstallStatus.Succeeded, reboot, $"Uninstalled through Windows Update{(reboot ? "; a restart completes it, after it the update shows as missing again" : string.Empty)}.")
                : new UninstallResult(UninstallStatus.Failed, reboot, string.Create(CultureInfo.InvariantCulture, $"Windows Update could not uninstall the update (result {code}, 0x{(int)result.HResult:X8})."));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            // Let DISM try; the failure is in the log through the caller's result.
            return null;
        }
    }
}

/// <summary>Runs a program and returns its exit code and standard output.</summary>
internal interface IProcessRunner
{
    (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);
}

internal sealed class ProcessRunner : IProcessRunner
{
    public (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }

        return (process.ExitCode, output.Result);
    }
}

/// <summary>
/// Finds the package of a KB with DISM (by its name, else by the support link in the package information, which also
/// covers cumulative updates named "RollupFix") and removes it without restarting.
/// </summary>
internal sealed partial class DismUninstall(IProcessRunner runner) : IDismUninstall
{
    /// <summary>Exit code of DISM when a restart completes the change.</summary>
    internal const int RestartRequired = 3010;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(50);

    private static string Dism => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

    public UninstallResult Uninstall(string kbArticle)
    {
        var (listCode, list) = runner.Run(Dism, ["/English", "/Online", "/Get-Packages", "/Format:Table"], QueryTimeout);
        if (listCode != 0)
        {
            return new UninstallResult(UninstallStatus.Failed, false, string.Create(CultureInfo.InvariantCulture, $"DISM could not list the packages (exit code {listCode})."));
        }

        var packages = InstalledPackages(list);
        var package = packages.FirstOrDefault(p => p.Contains($"KB{kbArticle}", StringComparison.OrdinalIgnoreCase))
            ?? packages.Where(p => p.StartsWith("Package_for_RollupFix", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Package_for_KB", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(p => MentionsKb(runner.Run(Dism, ["/English", "/Online", "/Get-PackageInfo", $"/PackageName:{p}"], QueryTimeout).Output, kbArticle));
        if (package is null)
        {
            return new UninstallResult(UninstallStatus.NotInstalled, false, $"No installed package of KB{kbArticle} was found (DISM).");
        }

        var (code, _) = runner.Run(Dism, ["/English", "/Online", "/Remove-Package", $"/PackageName:{package}", "/Quiet", "/NoRestart"], RemoveTimeout);
        return code switch
        {
            0 => new UninstallResult(UninstallStatus.Succeeded, false, $"Uninstalled with DISM ({package})."),
            RestartRequired => new UninstallResult(UninstallStatus.Succeeded, true, $"Uninstalled with DISM ({package}); a restart completes it, after it the update shows as missing again."),
            _ => new UninstallResult(UninstallStatus.Failed, false, string.Create(CultureInfo.InvariantCulture, $"DISM could not remove {package} (exit code {code}); some packages, e.g. servicing stack updates, cannot be removed.")),
        };
    }

    /// <summary>Package identities in state "Installed" from "dism /Get-Packages /Format:Table".</summary>
    internal static List<string> InstalledPackages(string table) =>
    [
        .. table.Split('\n')
            .Select(line => line.Split('|'))
            .Where(cells => cells.Length >= 2 && cells[1].Trim().Equals("Installed", StringComparison.OrdinalIgnoreCase))
            .Select(cells => cells[0].Trim())
            .Where(name => PackageRegex().IsMatch(name)),
    ];

    /// <summary>True if "dism /Get-PackageInfo" names the KB, e.g. in "Support Information : https://support.microsoft.com/help/5040434".</summary>
    internal static bool MentionsKb(string info, string kbArticle) =>
        info.Contains($"/help/{kbArticle}", StringComparison.OrdinalIgnoreCase) || info.Contains($"KB{kbArticle}", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^Package_for_[A-Za-z0-9_.~\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageRegex();
}
