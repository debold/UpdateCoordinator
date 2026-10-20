using Microsoft.Extensions.Options;

using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Updates;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Installation;

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

    /// <summary>Why the pending reboot waits, or that the agent restarts the server; null if no reboot is pending (live status).</summary>
    RebootWait? RebootWait { get; }
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
    private readonly ISystemRestart _restart;
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
        ISystemRestart restart,
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
        _restart = restart;
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

        CompleteLeaseAfterReboot();

        var batch = PlanBatch();
        if (batch.Count > 0)
        {
            // Never install on a server that fails its health check.
            if (_preFlight.Run().Any(check => check.Status == HealthStatus.Failed))
            {
                LogSkippedUnhealthy();
            }
            else
            {
                // In a coordinated group only a lease holder may install; without a free lease the batch waits.
                var lease = await EnsureLeaseAsync(identity, cancellationToken).ConfigureAwait(false);
                if (lease is null)
                {
                    return false;
                }

                if (lease.Value)
                {
                    var succeeded = await InstallAsync(batch, cancellationToken).ConfigureAwait(false);
                    if (_state.Lease is { } hold)
                    {
                        Save(_state with { Lease = hold with { InstallationSucceeded = hold.InstallationSucceeded && succeeded, AwaitingReboot = _environment.RebootPending } });
                    }

                    if (!await FlushOutboxAsync(identity, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
            }
        }

        return await RebootIfAllowedAsync(identity, cancellationToken).ConfigureAwait(false) is not null
            && await ReleaseLeaseIfDoneAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maintenance windows from the central server, or the local ones while it sends none.</summary>
    private IReadOnlyList<MaintenanceWindow> EffectiveWindows =>
        _releases.Current.MaintenanceWindows is { Count: > 0 } central ? central : _options.Value.LocalWindows;

    /// <summary>
    /// True if the server may install or reboot now: no coordination needed, a lease is already held or one was granted.
    /// Null if the central server rejected the credentials.
    /// </summary>
    private async Task<bool?> EnsureLeaseAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        if (!_releases.Current.RequiresLease || _state.Lease is not null)
        {
            return true;
        }

        var response = await _client.AcquireLeaseAsync(identity, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        if (!response.Granted)
        {
            LogLeaseWaiting(response.Reason ?? "no lease available");
            return false;
        }

        Save(_state with { Lease = new LeaseHold(_timeProvider.GetUtcNow(), InstallationSucceeded: true, AwaitingReboot: false) });
        return true;
    }

    /// <summary>A lease kept for a reboot ends once the server has started again and no reboot is pending any more.</summary>
    private void CompleteLeaseAfterReboot()
    {
        if (_state.Lease is { AwaitingReboot: true } hold && _environment.LastBootAt > hold.AcquiredAt && !_environment.RebootPending)
        {
            Save(_state with { Lease = hold with { AwaitingReboot = false } });
        }
    }

    /// <summary>Releases a held lease unless it is kept for a reboot that is still allowed to happen.</summary>
    private async Task<bool> ReleaseLeaseIfDoneAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        if (_state.Lease is not { } hold || (hold.AwaitingReboot && RebootMayFollow()))
        {
            return true;
        }

        if (!await _client.ReleaseLeaseAsync(identity, hold.InstallationSucceeded, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Save(_state with { Lease = null });
        LogLeaseReleased(hold.InstallationSucceeded);
        return true;
    }

    /// <summary>A reboot can still come from this agent: the policy allows reboots and no local veto applies.</summary>
    private bool RebootMayFollow() =>
        _releases.Current.RebootPolicy == RebootPolicy.InsideMaintenanceWindow && _options.Value.AllowReboot && _options.Value.Enabled;

    /// <summary>
    /// Restarts the server when a reboot is pending and the central policy, the local veto, the global pause and the
    /// maintenance window allow it (see <see cref="RebootPlanner"/>). In a coordinated group the restart needs a lease.
    /// Every request is recorded, so it is not repeated within the cooldown even if Windows refused it.
    /// Null if the central server rejected the credentials.
    /// </summary>
    internal async Task<RebootDecision?> RebootIfAllowedAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var decision = RebootPlanner.Decide(
            _releases.Current.RebootPolicy ?? RebootPolicy.Never,
            _options.Value,
            _releases.InstallationsPaused,
            _environment.RebootPending,
            EffectiveWindows,
            _timeProvider.GetLocalNow().DateTime,
            _state.LastRebootRequestAt,
            now);
        RebootWait = ToRebootWait(decision);
        if (decision != RebootDecision.Reboot)
        {
            return decision;
        }

        var lease = await EnsureLeaseAsync(identity, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return null;
        }

        if (!lease.Value)
        {
            RebootWait = Contracts.Agents.RebootWait.WaitingForLease;
            return RebootDecision.WaitingForLease;
        }

        Save(_state with { LastRebootRequestAt = now, Lease = _state.Lease is { } hold ? hold with { AwaitingReboot = true } : null });
        if (_restart.Schedule(RebootPlanner.WarningPeriod))
        {
            LogRebootScheduled(RebootPlanner.WarningPeriod);
        }

        return decision;
    }

    public RebootWait? RebootWait { get; private set; }

    internal static RebootWait? ToRebootWait(RebootDecision decision) => decision switch
    {
        RebootDecision.Reboot => Contracts.Agents.RebootWait.Restarting,
        RebootDecision.NotAllowedByPolicy => Contracts.Agents.RebootWait.NotAllowedByPolicy,
        RebootDecision.LocalVeto => Contracts.Agents.RebootWait.LocalVeto,
        RebootDecision.Paused => Contracts.Agents.RebootWait.Paused,
        RebootDecision.OutsideWindow => Contracts.Agents.RebootWait.OutsideWindow,
        RebootDecision.WindowEndsSoon => Contracts.Agents.RebootWait.WindowEndsSoon,
        RebootDecision.RecentlyRequested => Contracts.Agents.RebootWait.RecentlyRequested,
        RebootDecision.WaitingForLease => Contracts.Agents.RebootWait.WaitingForLease,
        _ => null,
    };

    internal IReadOnlyList<Release> PlanBatch()
    {
        var options = _options.Value;
        var missing = _updates.LastMissingUpdates;
        if (!options.Enabled
            || missing is null
            || !InstallationPlanner.IsInWindow(EffectiveWindows, _timeProvider.GetLocalNow().DateTime))
        {
            return [];
        }

        var attempts = _state.Attempts.ToDictionary(a => (a.UpdateId, a.Revision), a => a.AttemptedAt);
        return InstallationPlanner.PlanBatch(_releases.Current, _releases.InstallationsPaused, missing, attempts, _timeProvider.GetUtcNow(), options);
    }

    /// <summary>Installs a batch, buffers the report and returns true if every update succeeded and the server is healthy afterwards.</summary>
    private async Task<bool> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken)
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
        return failed == 0 && report.PostInstallChecks.All(check => check.Status != HealthStatus.Failed);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Installation finished: {Succeeded} succeeded, {Failed} not successful, reboot required: {RebootRequired}.")]
    private partial void LogInstalled(int succeeded, int failed, bool rebootRequired);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Due updates are not installed because the health check failed.")]
    private partial void LogSkippedUnhealthy();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A reboot is pending and allowed in this maintenance window: the server restarts in {Warning}.")]
    private partial void LogRebootScheduled(TimeSpan warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for a lease of the coordinated group: {Reason}.")]
    private partial void LogLeaseWaiting(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lease of the coordinated group released (succeeded: {Succeeded}).")]
    private partial void LogLeaseReleased(bool succeeded);
}
