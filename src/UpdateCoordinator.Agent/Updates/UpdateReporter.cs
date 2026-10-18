using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Wua;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Updates;

/// <summary>
/// Update run of the main loop: pre-flight check, search and report.
/// </summary>
internal interface IUpdateReporter
{
    /// <summary>Runs check, search and report if due. Returns false if the central server rejected the credentials.</summary>
    Task<bool> RunIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken);
}

internal sealed partial class UpdateReporter(
    PreFlightCheck preFlight,
    IAvailableUpdateSearch search,
    ICentralServerClient client,
    IOptions<UpdateOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateReporter> logger) : IUpdateReporter
{
    private DateTimeOffset? _lastRun;

    public async Task<bool> RunIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_lastRun is not null && now - _lastRun < options.Value.SearchInterval)
        {
            return true;
        }

        var report = await CreateReportAsync(now, cancellationToken).ConfigureAwait(false);
        if (!await client.SendUpdateReportAsync(identity, report, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Only a delivered report counts as a run; otherwise the next cycle tries again.
        _lastRun = now;
        LogReported(report.SearchSucceeded, report.AvailableUpdates?.Count ?? 0);
        return true;
    }

    internal async Task<UpdateReport> CreateReportAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var checks = preFlight.Run();
        if (checks.Any(check => check.Status == HealthStatus.Failed))
        {
            LogPreFlightFailed(string.Join(", ", checks.Where(c => c.Status == HealthStatus.Failed).Select(c => c.Name)));
            return new UpdateReport { StartedAt = startedAt, HealthChecks = checks };
        }

        try
        {
            var available = await search.SearchAsync(cancellationToken).ConfigureAwait(false);
            return new UpdateReport
            {
                StartedAt = startedAt,
                SearchSucceeded = true,
                HealthChecks = checks,
                AvailableUpdates = WireFormat.ToAvailableUpdates(available),
            };
        }
        catch (WuaException ex)
        {
            LogSearchFailed(ex.HResult, ex.Message);
            return new UpdateReport { StartedAt = startedAt, SearchHResult = ex.HResult, HealthChecks = checks };
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update report sent (search succeeded: {Succeeded}, {Count} available updates).")]
    private partial void LogReported(bool succeeded, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pre-flight check failed ({Checks}); no update search is run.")]
    private partial void LogPreFlightFailed(string checks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update search failed with 0x{HResult:X8}: {Reason}")]
    private partial void LogSearchFailed(int hresult, string reason);
}
