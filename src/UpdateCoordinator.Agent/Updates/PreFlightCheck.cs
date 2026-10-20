using System.Globalization;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Updates;

/// <summary>
/// Free space on the system drive; abstracted for tests.
/// </summary>
internal interface IDiskSpace
{
    /// <summary>Free bytes on the drive holding the Windows directory, or null if it cannot be determined.</summary>
    long? SystemDriveFreeBytes { get; }
}

internal sealed class DiskSpace : IDiskSpace
{
    public long? SystemDriveFreeBytes
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// Pre-flight health check before every update run (see docs/05-agent-design.md, section 3).
/// A failed check blocks search and installation; a warning does not.
/// </summary>
internal sealed class PreFlightCheck(ISystemProbe probe, IDiskSpace disk, IOptions<UpdateOptions> options)
{
    public const string DiskSpaceCheck = "DiskSpace";
    public const string UpdateServiceCheck = "UpdateService";
    public const string UpdateSourceCheck = "UpdateSource";
    public const string AutomaticUpdatesCheck = "AutomaticUpdates";

    private const string UpdateServicePath = @"SYSTEM\CurrentControlSet\Services\wuauserv";
    private const string ServiceStartDisabled = "4";

    public IReadOnlyList<HealthCheckResult> Run() => [CheckDiskSpace(), CheckUpdateService(), CheckUpdateSource(), CheckAutomaticUpdates()];

    private HealthCheckResult CheckDiskSpace()
    {
        var required = (long)options.Value.MinFreeDiskSpaceMegabytes * 1_048_576;
        var free = disk.SystemDriveFreeBytes;
        var freeText = free is null ? "unknown" : string.Create(CultureInfo.InvariantCulture, $"{free.Value / 1_048_576} MB");
        var status = free switch
        {
            null => HealthStatus.Warning,
            var bytes when bytes < required => HealthStatus.Failed,
            _ => HealthStatus.Ok,
        };
        return new HealthCheckResult
        {
            Name = DiskSpaceCheck,
            Status = status,
            Detail = string.Create(CultureInfo.InvariantCulture, $"Free space on system drive: {freeText}, required: {options.Value.MinFreeDiskSpaceMegabytes} MB."),
        };
    }

    private HealthCheckResult CheckUpdateService()
    {
        var start = probe.GetRegistryValue(UpdateServicePath, "Start");
        return start switch
        {
            null => new HealthCheckResult { Name = UpdateServiceCheck, Status = HealthStatus.Failed, Detail = "The Windows Update service (wuauserv) is not installed." },
            ServiceStartDisabled => new HealthCheckResult { Name = UpdateServiceCheck, Status = HealthStatus.Failed, Detail = "The Windows Update service (wuauserv) is disabled." },
            _ => new HealthCheckResult { Name = UpdateServiceCheck, Status = HealthStatus.Ok },
        };
    }

    private HealthCheckResult CheckUpdateSource()
    {
        var source = InventoryCollector.ReadUpdateSource(probe);
        if (!source.UsesWsus)
        {
            return new HealthCheckResult { Name = UpdateSourceCheck, Status = HealthStatus.Ok, Detail = "Windows Update." };
        }

        // WSUS is not desired, but not blocking (see docs/05-agent-design.md).
        var detail = options.Value.Source == UpdateSourceSelection.WindowsUpdate
            ? $"WSUS configured by policy ({source.WsusServer}); the agent searches Windows Update directly."
            : $"WSUS configured by policy ({source.WsusServer}); updates are searched there.";
        return new HealthCheckResult { Name = UpdateSourceCheck, Status = HealthStatus.Warning, Detail = detail.Length <= 512 ? detail : detail[..512] };
    }
    /// <summary>
    /// Windows installing or restarting on its own bypasses rings and maintenance windows: a warning, also where the central
    /// server lets the agent change the setting, because group policy may prevent it (see docs/05-agent-design.md, section 3).
    /// </summary>
    private HealthCheckResult CheckAutomaticUpdates()
    {
        var automatic = AutomaticUpdatesReader.Read(probe);
        var origin = automatic.ByPolicy ? "group policy" : "local setting";
        if (!automatic.BypassesCoordination())
        {
            return new HealthCheckResult { Name = AutomaticUpdatesCheck, Status = HealthStatus.Ok, Detail = $"Windows does not install on its own ({automatic.Mode}, {origin})." };
        }

        var reasons = new List<string>();
        if (automatic.Mode == AutomaticUpdateMode.ScheduledInstall)
        {
            reasons.Add($"Automatic Updates installs on its own schedule (AUOptions 4, {origin})");
        }

        if (automatic.ScheduledRestart)
        {
            reasons.Add("restarts automatically at the scheduled time");
        }

        if (automatic.Deadline)
        {
            reasons.Add("a compliance deadline forces installation and restart");
        }

        var detail = string.Join("; ", reasons) + ". Updates may be installed outside rings and maintenance windows; recommended: Configure Automatic Updates 3 or 7, or disabled.";
        return new HealthCheckResult { Name = AutomaticUpdatesCheck, Status = HealthStatus.Warning, Detail = detail.Length <= 512 ? detail : detail[..512] };
    }
}
