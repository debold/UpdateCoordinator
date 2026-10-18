using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Agent.Updates;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Installation;

/// <summary>
/// Installation step of the main loop.
/// </summary>
internal interface IInstallationRunner
{
    /// <summary>
    /// Delivers buffered install reports and installs a due batch inside the maintenance window.
    /// Returns false if the central server rejected the credentials.
    /// </summary>
    Task<bool> RunIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken);
}

internal sealed partial class InstallationRunner : IInstallationRunner
{
    /// <summary>Upper bound for buffered reports; the oldest are dropped beyond it.</summary>
    internal const int MaxOutbox = 50;

    private readonly IReleaseSync _releases;
    private readonly IUpdateReporter _updates;
    private readonly IInventoryReporter _inventory;
    private readonly IUpdateInstaller _installer;
    private readonly PreFlightCheck _preFlight;
    private readonly ICentralServerClient _client;
    private readonly IInstallationStateStore _store;
    private readonly IAgentEnvironment _environment;
    private readonly IOptions<InstallationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstallationRunner> _logger;
    private InstallationState _state;

    public InstallationRunner(
        IReleaseSync releases,
        IUpdateReporter updates,
        IInventoryReporter inventory,
        IUpdateInstaller installer,
        PreFlightCheck preFlight,
        ICentralServerClient client,
        IInstallationStateStore store,
        IAgentEnvironment environment,
        IOptions<InstallationOptions> options,
        TimeProvider timeProvider,
        ILogger<InstallationRunner> logger)
    {
        _releases = releases;
        _updates = updates;
        _inventory = inventory;
        _installer = installer;
        _preFlight = preFlight;
        _client = client;
        _store = store;
        _environment = environment;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _state = store.Load();
    }

    public async Task<bool> RunIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        if (!await FlushOutboxAsync(identity, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var batch = PlanBatch();
        if (batch.Count == 0)
        {
            return true;
        }

        // Never install on a server that fails its health check.
        if (_preFlight.Run().Any(check => check.Status == HealthStatus.Failed))
        {
            LogSkippedUnhealthy();
            return true;
        }

        await InstallAsync(batch, cancellationToken).ConfigureAwait(false);
        return await FlushOutboxAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    internal IReadOnlyList<Release> PlanBatch()
    {
        var options = _options.Value;
        var missing = _updates.LastMissingUpdates;
        if (!options.Enabled
            || missing is null
            || !InstallationPlanner.IsInWindow(_releases.Current.MaintenanceWindows is { Count: > 0 } central ? central : options.LocalWindows, _timeProvider.GetLocalNow().DateTime))
        {
            return [];
        }

        var attempts = _state.Attempts.ToDictionary(a => (a.UpdateId, a.Revision), a => a.AttemptedAt);
        return InstallationPlanner.PlanBatch(_releases.Current, _releases.InstallationsPaused, missing, attempts, _timeProvider.GetUtcNow(), options);
    }

    private async Task InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetUtcNow();
        LogInstalling(batch.Count);
        var results = await _installer.InstallAsync(batch, cancellationToken).ConfigureAwait(false);
        var finished = _timeProvider.GetUtcNow();

        var report = new InstallReport
        {
            RunId = Guid.NewGuid(),
            StartedAt = started,
            FinishedAt = finished,
            Results = [.. results.Take(InstallReport.MaxResults)],
            RebootRequired = results.Any(r => r.RebootRequired),
            OsVersionAfter = _environment.OsVersion,
            PostInstallChecks = _preFlight.Run(),
        };

        var attempted = batch.Select(r => (r.UpdateId, r.Revision)).ToHashSet();
        var outbox = _state.Outbox.Append(report).TakeLast(MaxOutbox).ToList();
        var attempts = _state.Attempts.Where(a => !attempted.Contains((a.UpdateId, a.Revision)))
            .Concat(batch.Select(r => new InstallAttempt(r.UpdateId, r.Revision, started)))
            .ToList();
        Save(_state with { Outbox = outbox, Attempts = attempts });

        // Installed state changed: refresh inventory and search with the next cycles.
        _inventory.Reset();
        _updates.RequestRun();

        var failed = results.Count(r => r.ResultCode is not (InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors));
        LogInstalled(results.Count - failed, failed, report.RebootRequired);
    }

    private async Task<bool> FlushOutboxAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        while (_state.Outbox.Count > 0)
        {
            var report = _state.Outbox[0];
            if (!await _client.SendInstallReportAsync(identity, report, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            Save(_state with { Outbox = [.. _state.Outbox.Skip(1)] });
        }

        return true;
    }

    private void Save(InstallationState state)
    {
        _store.Save(state);
        _state = state;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing {Count} released updates.")]
    private partial void LogInstalling(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installation finished: {Succeeded} succeeded, {Failed} not successful, reboot required: {RebootRequired}. The agent does not reboot on its own.")]
    private partial void LogInstalled(int succeeded, int failed, bool rebootRequired);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Due updates are not installed because the health check failed.")]
    private partial void LogSkippedUnhealthy();
}
