using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;

using Microsoft.Extensions.Options;

using SUSyphos.Agent.Actions;

namespace SUSyphos.Agent;

/// <summary>Resource limits of the agent (docs/02-guidelines.md, section 9). Bound from the "Resources" configuration section.</summary>
internal sealed class ResourceOptions
{
    public const string SectionName = "Resources";

    /// <summary>Private memory above which the agent restarts its service (service recovery starts it again).</summary>
    [Range(64, 4096)]
    public int MaxPrivateMemoryMegabytes { get; set; } = 200;

    /// <summary>Runs the agent process with below-normal priority, so it never competes with the server's workload.</summary>
    public bool LowPriority { get; set; } = true;
}

/// <summary>The running agent process; separate so tests never change the test host's process.</summary>
internal interface IProcessResources
{
    long PrivateMemoryBytes { get; }

    void CollectGarbage();

    void LowerPriority();
}

internal sealed class CurrentProcessResources : IProcessResources
{
    public long PrivateMemoryBytes
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64;
        }
    }

    public void CollectGarbage() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    public void LowerPriority()
    {
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
    }
}

/// <summary>
/// Keeps the agent within its limits: low process priority, and a restart when private memory stays above the limit
/// after a full garbage collection (a leak, e.g. in WUA COM objects, must never grow until it endangers the server).
/// </summary>
internal sealed partial class ResourceGuard(
    IProcessResources process,
    IServiceRestarter restarter,
    IOptions<ResourceOptions> options,
    ILogger<ResourceGuard> logger)
{
    private const long Megabyte = 1024 * 1024;

    public void ApplyPriority()
    {
        if (!options.Value.LowPriority)
        {
            return;
        }

        try
        {
            process.LowerPriority();
            LogPriorityLowered();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            LogPriorityFailed(ex);
        }
    }

    /// <summary>True while the agent stays within its memory limit; otherwise requests a service restart and returns false.</summary>
    public bool CheckMemory()
    {
        var limit = options.Value.MaxPrivateMemoryMegabytes * Megabyte;
        if (process.PrivateMemoryBytes <= limit)
        {
            return true;
        }

        process.CollectGarbage();
        var used = process.PrivateMemoryBytes;
        if (used <= limit)
        {
            return true;
        }

        LogMemoryExceeded(used / Megabyte, options.Value.MaxPrivateMemoryMegabytes);
        restarter.RequestRestart();
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent runs with below-normal process priority.")]
    private partial void LogPriorityLowered();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The process priority could not be lowered; the agent keeps running with normal priority.")]
    private partial void LogPriorityFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The agent uses {UsedMegabytes} MB private memory, above its limit of {LimitMegabytes} MB; the service restarts.")]
    private partial void LogMemoryExceeded(long usedMegabytes, int limitMegabytes);
}
