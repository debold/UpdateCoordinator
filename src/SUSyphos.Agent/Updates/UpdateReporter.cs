using Microsoft.Extensions.Options;

using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Wua;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Updates;

/// <summary>
/// Update run of the main loop: pre-flight check, search and report.
/// </summary>
internal interface IUpdateReporter
{
    /// <summary>Runs check, search and report if due. Returns false if the central server rejected the credentials.</summary>
    Task<bool> RunIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken);

    /// <summary>Updates the last successful search found missing; null before the first successful search.</summary>
    IReadOnlySet<(Guid UpdateId, int Revision)>? LastMissingUpdates { get; }

    /// <summary>Makes the next cycle run check, search and report regardless of the interval (e.g. after an installation).</summary>
    void RequestRun();

    /// <summary>When the next search is due; null if it runs with the next cycle.</summary>
    DateTimeOffset? NextRunAt { get; }
}

internal sealed partial class UpdateReporter(
    PreFlightCheck preFlight,
    IAvailableUpdateSearch search,
    IReleaseSync releases,
    ICentralServerClient client,
    Status.ActivityTracker activity,
    IOptions<UpdateOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateReporter> logger) : IUpdateReporter
{
    /// <summary>Released updates looked up per search at most; the lookup asks for four states per update.</summary>
    internal const int MaxLookups = 100;

    private DateTimeOffset? _lastRun;

    public IReadOnlySet<(Guid UpdateId, int Revision)>? LastMissingUpdates { get; private set; }

    public void RequestRun() => _lastRun = null;

    public DateTimeOffset? NextRunAt => _lastRun + options.Value.SearchInterval;

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
        if (report is { SearchSucceeded: true, AvailableUpdates: { } available })
        {
            LastMissingUpdates = available.Select(u => (u.UpdateId, u.Revision)).ToHashSet();
        }

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
            IReadOnlyList<Wua.WuaUpdate> available;
            IReadOnlyList<(Guid UpdateId, int Revision)>? notOffered;
            using (activity.Begin(AgentPhase.Searching))
            {
                available = await search.SearchAsync(cancellationToken).ConfigureAwait(false);
                notOffered = await FindNotOfferedAsync(available, cancellationToken).ConfigureAwait(false);
            }

            return new UpdateReport
            {
                StartedAt = startedAt,
                SearchSucceeded = true,
                HealthChecks = checks,
                AvailableUpdates = WireFormat.ToAvailableUpdates(available),
                NotOffered = notOffered?.Select(u => new UpdateKey(u.UpdateId, u.Revision)).ToList(),
            };
        }
        catch (WuaException ex)
        {
            LogSearchFailed(ex.HResult, ex.Message);
            return new UpdateReport { StartedAt = startedAt, SearchHResult = ex.HResult, HealthChecks = checks };
        }
    }

    /// <summary>
    /// Released updates the search did not find, looked up by ID: those Windows Update does not know at all any more
    /// (withdrawn by Microsoft, or no longer applicable), so the central server can stop a faulty rollout. A failed
    /// lookup reports nothing rather than a wrong withdrawal.
    /// </summary>
    private async Task<IReadOnlyList<(Guid UpdateId, int Revision)>?> FindNotOfferedAsync(IReadOnlyList<Wua.WuaUpdate> available, CancellationToken cancellationToken)
    {
        var offered = available.Select(u => (Guid.TryParse(u.UpdateId, out var id) ? id : Guid.Empty, u.Revision)).ToHashSet();
        var released = releases.Current.Releases
            .Select(r => (r.UpdateId, r.Revision))
            .Where(r => !offered.Contains(r))
            .Distinct()
            .Take(MaxLookups)
            .ToList();
        if (released.Count == 0)
        {
            return [];
        }

        try
        {
            return await search.FindUnknownAsync(released, cancellationToken).ConfigureAwait(false);
        }
        catch (WuaException ex)
        {
            LogLookupFailed(ex.HResult, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking up released updates the search did not find failed with 0x{HResult:X8}: {Reason}")]
    private partial void LogLookupFailed(int hresult, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update report sent (search succeeded: {Succeeded}, {Count} available updates).")]
    private partial void LogReported(bool succeeded, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pre-flight check failed ({Checks}); no update search is run.")]
    private partial void LogPreFlightFailed(string checks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update search failed with 0x{HResult:X8}: {Reason}")]
    private partial void LogSearchFailed(int hresult, string reason);
}
