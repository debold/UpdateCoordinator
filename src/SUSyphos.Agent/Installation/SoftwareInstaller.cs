using System.Text.Json;

using Microsoft.Extensions.Options;

using SUSyphos.Agent.Inventory;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Installation;

/// <summary>
/// Sends each release of a batch to its installer: Windows updates to Windows Update, software versions to WinGet. One
/// result per release, in the order of the batch kinds.
/// </summary>
internal sealed class RoutingUpdateInstaller(WuaUpdateInstaller windows, SoftwareInstaller software) : IUpdateInstaller
{
    public async Task<IReadOnlyList<InstallResult>> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var results = new List<InstallResult>();
        var updates = batch.Where(r => r.Software is null).ToList();
        if (updates.Count > 0)
        {
            results.AddRange(await windows.InstallAsync(updates, cancellationToken).ConfigureAwait(false));
        }

        var programs = batch.Where(r => r.Software is not null).ToList();
        if (programs.Count > 0)
        {
            results.AddRange(await software.InstallAsync(programs, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}

/// <summary>
/// Installs released software versions with WinGet in the helper process (SUSyphos.Agent.WinGet.exe install), from the
/// WinGet source of the central server, silently for the machine. Of several released versions of one package only the
/// newest is installed; the older ones are answered as superseded, since the newest replaces them. Never restarts.
/// </summary>
internal sealed partial class SoftwareInstaller(IWinGetHelperRunner runner, IOptions<CentralServerOptions> central, ILogger<SoftwareInstaller> logger)
{
    /// <summary>An installer may take its time; WinGet downloads it from the central server first.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <summary>HRESULT reported for a software installation that failed (E_FAIL); the detail is in the agent log.</summary>
    internal const int Failed = unchecked((int)0x80004005);

    public async Task<IReadOnlyList<InstallResult>> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken)
    {
        var results = new List<InstallResult>();
        foreach (var package in batch.Where(r => r.Software is not null).GroupBy(r => r.Software!.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = package.OrderByDescending(r => r.Software!.Version, WinGetVersionOrder.Instance).ToList();
            results.Add(await InstallOneAsync(ordered[0], cancellationToken).ConfigureAwait(false));
            results.AddRange(ordered.Skip(1).Select(r => new InstallResult { UpdateId = r.UpdateId, Revision = r.Revision, ResultCode = InstallResultCode.Succeeded, HResult = 0, RebootRequired = false }));
        }

        return results;
    }

    private async Task<InstallResult> InstallOneAsync(Release release, CancellationToken cancellationToken)
    {
        var software = release.Software!;
        if (central.Value.BaseAddress is not { } address)
        {
            return Result(release, false, false);
        }

        List<string> arguments = ["install", software.PackageId, software.Version, new Uri(address, "winget/api/").AbsoluteUri];
        if (software.AcceptAgreements)
        {
            arguments.Add("accept-agreements");
        }

        try
        {
            var finished = await runner.RunAsync(arguments, Timeout, cancellationToken).ConfigureAwait(false);
            if (finished is not { ExitCode: 0 } done)
            {
                LogFailed(software.PackageId, software.Version, finished is null ? "the WinGet helper did not finish in time" : $"helper exit code {finished.Value.ExitCode}");
                return Result(release, false, false);
            }

            var outcome = JsonSerializer.Deserialize<SoftwareInstallOutcome>(done.Output, SoftwareInventoryReport.JsonOptions);
            if (outcome is null)
            {
                LogFailed(software.PackageId, software.Version, "the WinGet helper wrote no result");
                return Result(release, false, false);
            }

            if (outcome.Succeeded)
            {
                LogInstalled(software.PackageId, software.Version, outcome.RebootRequired);
            }
            else
            {
                LogFailed(software.PackageId, software.Version, $"{outcome.Status}, installer exit code {outcome.InstallerExitCode}: {outcome.Detail}");
            }

            return Result(release, outcome.Succeeded, outcome.RebootRequired);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or JsonException)
        {
            LogFailed(software.PackageId, software.Version, exception.Message);
            return Result(release, false, false);
        }
    }

    private static InstallResult Result(Release release, bool succeeded, bool rebootRequired) => new()
    {
        UpdateId = release.UpdateId,
        Revision = release.Revision,
        ResultCode = succeeded ? InstallResultCode.Succeeded : InstallResultCode.Failed,
        HResult = succeeded ? 0 : Failed,
        RebootRequired = rebootRequired,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Software {PackageId} {Version} installed with WinGet (restart required: {RebootRequired}).")]
    private partial void LogInstalled(string packageId, string version, bool rebootRequired);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Software {PackageId} {Version} could not be installed with WinGet: {Reason}.")]
    private partial void LogFailed(string packageId, string version, string reason);
}

/// <summary>Orders versions the way people read them: numeric parts as numbers ("26.10" after "26.9"), the rest as text.</summary>
internal sealed class WinGetVersionOrder : IComparer<string>
{
    public static readonly WinGetVersionOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        var left = (x ?? string.Empty).Split('.', '-', '+');
        var right = (y ?? string.Empty).Split('.', '-', '+');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : "0";
            var b = i < right.Length ? right[i] : "0";
            var order = long.TryParse(a, out var na) && long.TryParse(b, out var nb) ? na.CompareTo(nb) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}
