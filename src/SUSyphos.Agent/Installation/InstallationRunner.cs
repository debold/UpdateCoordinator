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

    /// <summary>True while an administrator's request to restart in the maintenance window is held.</summary>
    bool RestartRequested { get; }

    /// <summary>
    /// True when Windows reports a pending reboot or an installation since the last start asked for one (exit code 3010 or
    /// 1641 of an installer sets none of the keys Windows Update uses; rc.7 lab, LABVDC01, 2026-11-22: VC++ 2013 waited for a
    /// restart that never came). Reported with the heartbeat.
    /// </summary>
    bool RebootPending { get; }

    /// <summary>Holds a restart for the next maintenance window that stays open long enough (remote action); returns the planned time.</summary>
    DateTimeOffset? RequestRestart();

    /// <summary>Drops a held restart request and cancels a restart already announced by this agent; returns what was cancelled.</summary>
    string CancelRestart();

    /// <summary>When the held restart is planned; null without request or without windows.</summary>
    DateTimeOffset? PlannedRestartAt { get; }

    /// <summary>When the restart this agent announced to Windows happens (the warning period runs); null when none is announced.</summary>
    DateTimeOffset? RestartAnnouncedAt { get; }

    /// <summary>Exit code of the shutdown tool when Windows refused the last restart request; null otherwise.</summary>
    int? RestartRefusedCode { get; }

    /// <summary>Installs every released update the server misses with the next cycle (remote action); a needed restart follows at once or in the next window.</summary>
    void RequestInstallNow(bool restartNow, InstallScope scope = InstallScope.All, IReadOnlyList<Guid>? updateIds = null, Guid? actionId = null);

    /// <summary>
    /// Holds an uninstallation (remote action) until it may run: in a coordinated group with the lease, for
    /// <see cref="AgentActionKind.UninstallInWindow"/> inside the maintenance window. Its result goes to the central server then.
    /// </summary>
    void RequestUninstall(AgentAction action);
}

internal sealed partial class InstallationRunner : IInstallationRunner
{
    /// <summary>Upper bound for buffered reports; the oldest are dropped beyond it.</summary>
    internal const int MaxOutbox = 50;

    private readonly IReleaseSync _releases;
    private readonly IUpdateReporter _updates;
    private readonly IInventoryReporter _inventory;
    private readonly IUpdateInstaller _installer;
    private readonly IUpdateUninstaller _uninstaller;
    private readonly PreFlightCheck _preFlight;
    private readonly ICentralServerClient _client;
    private readonly IInstallationStateStore _store;
    private readonly IAgentEnvironment _environment;
    private readonly ISystemRestart _restart;
    private readonly IOptions<InstallationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstallationRunner> _logger;
    private readonly ISoftwareUninstaller? _softwareUninstaller;
    private readonly Status.ActivityTracker? _activity;
    private InstallationState _state;

    public InstallationRunner(
        IReleaseSync releases,
        IUpdateReporter updates,
        IInventoryReporter inventory,
        IUpdateInstaller installer,
        IUpdateUninstaller uninstaller,
        PreFlightCheck preFlight,
        ICentralServerClient client,
        IInstallationStateStore store,
        IAgentEnvironment environment,
        ISystemRestart restart,
        IOptions<InstallationOptions> options,
        TimeProvider timeProvider,
        ILogger<InstallationRunner> logger,
        ISoftwareUninstaller? softwareUninstaller = null,
        Status.ActivityTracker? activity = null)
    {
        _softwareUninstaller = softwareUninstaller;
        _activity = activity;
        _releases = releases;
        _updates = updates;
        _inventory = inventory;
        _installer = installer;
        _uninstaller = uninstaller;
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
        CompleteRequestedRestart();
        if (!await RunUninstallsAsync(identity, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // "Install now" waits only for a pause; otherwise it runs with this cycle, done or not, and is cleared.
        var installNow = _state.InstallNow;
        if (installNow is not null && _releases.InstallationsPaused)
        {
            installNow = null;
        }

        var batch = installNow is null ? PlanBatch() : PlanInstallNow(installNow);
        var waitingForLease = false;
        var unhealthy = false;
        if (batch.Count > 0)
        {
            // Never install on a server that fails its health check.
            if (_preFlight.Run().Any(check => check.Status == HealthStatus.Failed))
            {
                unhealthy = true;
                LogSkippedUnhealthy();
            }
            else
            {
                // Software is not coordinated (Marc, 2026-11-13): it installs without a lease, and its result does not
                // count for the lease, so a failed package never stops a coordinated group. A restart it needs still
                // waits for the lease like every restart.
                IReadOnlyList<Release> software = [.. batch.Where(r => r.Software is not null)];
                IReadOnlyList<Release> updates = [.. batch.Where(r => r.Software is null)];
                if (software.Count > 0)
                {
                    await InstallAsync(software, cancellationToken).ConfigureAwait(false);
                    if (!await FlushOutboxAsync(identity, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                // In a coordinated group only a lease holder may install Windows updates; without a free lease they wait.
                var lease = updates.Count == 0 ? false : await EnsureLeaseAsync(identity, cancellationToken).ConfigureAwait(false);
                if (lease is null)
                {
                    return false;
                }

                if (updates.Count == 0 && installNow is not null)
                {
                    FinishInstallNow(installNow);
                    installNow = null;
                }

                // "Install now" waits for the lease instead of being dropped (lab, LABVDC01, 2026-11-13); the software it
                // already installed is not installed again while it waits.
                if (!lease.Value && updates.Count > 0)
                {
                    waitingForLease = true;
                    if (installNow is not null && software.Count > 0)
                    {
                        Save(_state with { InstallNow = (_state.InstallNow ?? installNow) with { Scope = InstallScope.WindowsUpdates } });
                    }
                }

                if (lease.Value)
                {
                    var succeeded = await InstallAsync(updates, cancellationToken).ConfigureAwait(false);
                    if (installNow is not null)
                    {
                        FinishInstallNow(installNow);
                        installNow = null;
                    }

                    if (_state.Lease is { } hold)
                    {
                        Save(_state with { Lease = hold with { InstallationSucceeded = hold.InstallationSucceeded && succeeded, AwaitingReboot = RebootPending } });
                    }

                    if (!await FlushOutboxAsync(identity, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
            }
        }

        // Nothing to install or the server failed its health check: the request is answered with what is there.
        if (installNow is not null && !waitingForLease && (batch.Count == 0 || _state.InstallNow is not null))
        {
            FinishInstallNow(installNow, unhealthy ? "Nothing was installed: the server failed its health check. " : batch.Count == 0 ? "No released update was missing. " : null);
        }

        return await FlushActionOutboxAsync(identity, cancellationToken).ConfigureAwait(false)
            && await RebootIfAllowedAsync(identity, cancellationToken).ConfigureAwait(false) is not null
            && await ReleaseLeaseIfDoneAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maintenance windows from the central server, or the local ones while it sends none; one-time windows of the central
    /// server come on top, one entry per local day, so every calculation on windows sees them.
    /// </summary>
    private IReadOnlyList<MaintenanceWindow> EffectiveWindows
    {
        get
        {
            var weekly = _releases.Current.MaintenanceWindows is { Count: > 0 } central ? central : _options.Value.LocalWindows;
            return _releases.Current.OneTimeWindows is { Count: > 0 } once
                ? [.. weekly, .. once.Where(w => w.IsValid).SelectMany(w => w.ToLocalDays(_timeProvider.LocalTimeZone))]
                : weekly;
        }
    }

    /// <summary>
    /// The windows for software: its own software windows plus the one-time windows when the central server sends some,
    /// otherwise the maintenance windows (Marc, 2026-11-13; uninstallations since 2026-11-14).
    /// </summary>
    private IReadOnlyList<MaintenanceWindow> EffectiveSoftwareWindows =>
        _releases.Current.SoftwareWindows is { Count: > 0 } software
            ? [.. software, .. EffectiveWindows.Except(_releases.Current.MaintenanceWindows is { Count: > 0 } central ? central : _options.Value.LocalWindows)]
            : EffectiveWindows;

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
            // Logged when the reason changes and then once an hour, not with every cycle (LABVEX01, 2026-11-13: every 10 seconds).
            var reason = response.Reason ?? "no lease available";
            var now = _timeProvider.GetUtcNow();
            if (reason != _leaseWaitLogged || now - _leaseWaitLoggedAt >= TimeSpan.FromHours(1))
            {
                LogLeaseWaiting(reason);
                (_leaseWaitLogged, _leaseWaitLoggedAt) = (reason, now);
            }

            return false;
        }

        _leaseWaitLogged = null;

        Save(_state with { Lease = new LeaseHold(_timeProvider.GetUtcNow(), InstallationSucceeded: true, AwaitingReboot: false) });
        return true;
    }

    private string? _leaseWaitLogged;
    private DateTimeOffset _leaseWaitLoggedAt;

    public bool RestartRequested => _state.RestartRequestedAt is not null;

    /// <inheritdoc />
    public bool RebootPending => _environment.RebootPending || (_state.InstallerRestartAt is { } asked && _environment.LastBootAt <= asked);

    /// <summary>Remembers that an installation or uninstallation asked for a restart, until the server has started again.</summary>
    private void RememberInstallerRestart()
    {
        Save(_state with { InstallerRestartAt = _timeProvider.GetUtcNow() });
        LogInstallerRestart();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "An installation asked for a restart; it counts as pending until the server has started again.")]
    private partial void LogInstallerRestart();

    public DateTimeOffset? RequestRestart()
    {
        // An explicit request is not held back by the cooldown of an earlier, automatic request.
        Save(_state with { RestartRequestedAt = _timeProvider.GetUtcNow(), LastRebootRequestAt = null });
        return PlannedRestartAt;
    }

    public string CancelRestart()
    {
        var held = _state.RestartRequestedAt is not null;
        var announced = _state.LastRebootRequestAt is { } last && _timeProvider.GetUtcNow() - last < _options.Value.RestartWarning;
        Save(_state with { RestartRequestedAt = null });
        var aborted = announced && _restart.Abort();
        return (held, aborted) switch
        {
            (_, true) => "The announced restart was cancelled.",
            (true, false) => "The requested restart was cancelled.",
            _ => "No restart was requested or announced.",
        };
    }

    public DateTimeOffset? RestartAnnouncedAt => _restart.AnnouncedFor;

    /// <summary>Exit code of the shutdown tool when Windows refused the last restart request; null otherwise.</summary>
    public int? RestartRefusedCode => _state.RestartRefusedCode;

    public DateTimeOffset? PlannedRestartAt
    {
        get
        {
            if (_state.RestartRequestedAt is null)
            {
                return null;
            }

            if (RebootWait == Contracts.Agents.RebootWait.Restarting && _state.LastRebootRequestAt is { } requested)
            {
                return requested + _options.Value.RestartWarning;
            }

            var localNow = _timeProvider.GetLocalNow();
            var windows = EffectiveWindows;
            var probe = localNow.DateTime;
            for (var i = 0; i < 14; i++)
            {
                if (MaintenanceSchedule.NextWindow(windows, probe) is not { } window)
                {
                    return null;
                }

                if (window.End - window.Start >= RebootPlanner.MinimumRemainingWindow)
                {
                    var start = window.Start;
                    return new DateTimeOffset(start, _timeProvider.LocalTimeZone.GetUtcOffset(start));
                }

                probe = window.End.AddMinutes(1);
            }

            return null;
        }
    }

    /// <summary>A held restart request is done once the server has started after it.</summary>
    private void CompleteRequestedRestart()
    {
        if (_state.RestartRequestedAt is { } requested && _environment.LastBootAt > requested)
        {
            Save(_state with { RestartRequestedAt = null });
        }

        if (_state.RestartRefusedCode is not null && _state.LastRebootRequestAt is { } asked && _environment.LastBootAt > asked)
        {
            Save(_state with { RestartRefusedCode = null });
        }

        if (_state.InstallerRestartAt is { } installer && _environment.LastBootAt > installer)
        {
            Save(_state with { InstallerRestartAt = null });
        }
    }

    /// <summary>A lease kept for a reboot ends once the server has started again and no reboot is pending any more.</summary>
    private void CompleteLeaseAfterReboot()
    {
        if (_state.Lease is { AwaitingReboot: true } hold && _environment.LastBootAt > hold.AcquiredAt && !RebootPending)
        {
            Save(_state with { Lease = hold with { AwaitingReboot = false } });
        }
    }

    /// <summary>
    /// Releases a held lease unless it is kept for a reboot that is still due and allowed to happen. A reboot Windows no
    /// longer asks for does not keep the lease (lab, LABVEX01, 2026-11-13: Windows reported a restart for a moment after
    /// an installation, the lease was kept until it expired and the group stopped).
    /// </summary>
    private async Task<bool> ReleaseLeaseIfDoneAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var rebootDue = RebootPending || _state.RestartRequestedAt is not null || _restart.AnnouncedFor is not null;
        if (_state.Lease is not { } hold || (hold.AwaitingReboot && rebootDue && RebootMayFollow()))
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

    /// <summary>
    /// A reboot can still come from this agent: the policy allows reboots, a restart is held for the window, or the agent
    /// announced one (restart right after an uninstallation or "install now"); and no local veto applies.
    /// </summary>
    private bool RebootMayFollow() =>
        (_releases.Current.RebootPolicy == RebootPolicy.InsideMaintenanceWindow || _state.RestartRequestedAt is not null || _restart.AnnouncedFor is not null)
        && _options.Value.AllowReboot && _options.Value.Enabled;

    /// <summary>
    /// Restarts the server when a reboot is pending and the central policy, the local veto, the global pause and the
    /// maintenance window allow it (see <see cref="RebootPlanner"/>). In a coordinated group the restart needs a lease.
    /// Every request is recorded, so it is not repeated within the cooldown even if Windows refused it.
    /// Null if the central server rejected the credentials.
    /// </summary>
    internal async Task<RebootDecision?> RebootIfAllowedAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        // A restart an administrator requested for the window counts as pending and allowed by policy.
        var requested = _state.RestartRequestedAt is not null;
        var decision = RebootPlanner.Decide(
            requested ? RebootPolicy.InsideMaintenanceWindow : _releases.Current.RebootPolicy ?? RebootPolicy.Never,
            _options.Value,
            _releases.InstallationsPaused,
            RebootPending || requested,
            EffectiveWindows,
            _timeProvider.GetLocalNow().DateTime,
            _state.LastRebootRequestAt,
            now);
        RebootWait = decision == RebootDecision.RecentlyRequested && _state.RestartRefusedCode is not null
            ? Contracts.Agents.RebootWait.RestartRefused
            : ToRebootWait(decision);
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
        if (_restart.Schedule(_options.Value.RestartWarning))
        {
            Save(_state with { RestartRefusedCode = null });
            LogRebootScheduled(_options.Value.RestartWarning);
        }
        else
        {
            // Reported with the heartbeat, so the dashboard names it instead of only "did not happen".
            Save(_state with { RestartRefusedCode = _restart.LastRefusal ?? -1 });
            RebootWait = Contracts.Agents.RebootWait.RestartRefused;
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

    public void RequestInstallNow(bool restartNow, InstallScope scope = InstallScope.All, IReadOnlyList<Guid>? updateIds = null, Guid? actionId = null)
    {
        // A newer request replaces an open one; the open one is answered with what it installed so far.
        if (_state.InstallNow is { ActionId: { } open } previous)
        {
            if (open == actionId)
            {
                // The same action delivered again (the agent restarted before it answered): it keeps running.
                return;
            }

            Save(_state with { ActionOutbox = [.. _state.ActionOutbox ?? [], new ActionOutcome(open, InstallNowOutcome(previous, "Replaced by a newer \"Install now\". "))] });
        }

        Save(_state with { InstallNow = new InstallNowRequest(_timeProvider.GetUtcNow(), restartNow, scope, updateIds, actionId) });
        _updates.RequestRun();
    }

    private IReadOnlyList<Release> PlanInstallNow(InstallNowRequest request)
    {
        var missing = WithSoftware(WithoutAwaitingRestart(_updates.LastMissingUpdates));
        var scope = request.Scope ?? InstallScope.All;
        var selected = request.UpdateIds is { } ids ? ids.ToHashSet() : null;
        return !_options.Value.Enabled || missing is null
            ? []
            : [.. InstallationPlanner.PlanBatch(_releases.Current, _releases.InstallationsPaused, missing, new Dictionary<(Guid, int), DateTimeOffset>(), _timeProvider.GetUtcNow(), _options.Value, everything: true)
                .Where(r => scope switch
                {
                    InstallScope.WindowsUpdates => r.Software is null,
                    InstallScope.Software => r.Software is not null,
                    _ => true,
                })
                .Where(r => selected is null || selected.Contains(r.UpdateId))];
    }

    /// <summary>Clears the request and, if Windows wants a restart, restarts now (5-minute warning) or holds it for the next window; never against the local veto.</summary>
    /// <summary>
    /// The answer to "Install now": how many installations succeeded and failed, and what happens with a needed restart.
    /// Failed when one installation failed, so the dashboard shows it like any failed action.
    /// </summary>
    internal AgentActionResult InstallNowOutcome(InstallNowRequest request, string? note)
    {
        var counts = request.Succeeded + request.Failed == 0
            ? note ?? "Nothing was installed. "
            : $"{note}{request.Succeeded} succeeded, {request.Failed} failed. ";
        var restart = !RebootPending ? string.Empty
            : !_options.Value.AllowReboot ? "Reboots are forbidden on this server (AllowReboot), so the needed restart is left to an administrator."
            : request.RestartNow ? "The needed restart follows right after, with a five minute warning."
            : "The needed restart waits for the next maintenance window.";
        return new AgentActionResult
        {
            Status = request.Failed > 0 ? AgentActionStatus.Failed : AgentActionStatus.Succeeded,
            Detail = (counts + restart).Trim(),
        };
    }

    private void FinishInstallNow(InstallNowRequest request, string? note = null)
    {
        // The counts of the installations are in the saved request, the one passed may be older.
        var done = _state.InstallNow ?? request;
        Save(_state with { InstallNow = null });
        if (done.ActionId is { } actionId)
        {
            Save(_state with { ActionOutbox = [.. _state.ActionOutbox ?? [], new ActionOutcome(actionId, InstallNowOutcome(done, note))] });
        }

        if (!RebootPending)
        {
            return;
        }

        if (!_options.Value.AllowReboot)
        {
            LogInstallNowNoReboot();
            return;
        }

        if (request.RestartNow)
        {
            if (_restart.Schedule(_options.Value.RestartWarning))
            {
                Save(_state with { LastRebootRequestAt = _timeProvider.GetUtcNow() });
            }
        }
        else
        {
            RequestRestart();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Installed on request, but reboots are forbidden on this server (AllowReboot); the restart is left to an administrator.")]
    private partial void LogInstallNowNoReboot();

    public void RequestUninstall(AgentAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.UpdateId is not { } updateId || (_state.Uninstalls ?? []).Any(u => u.ActionId == action.Id))
        {
            return;
        }

        Save(_state with { Uninstalls = [.. _state.Uninstalls ?? [], new UninstallRequest(action.Id, action.Kind, updateId, action.KbArticle, _timeProvider.GetUtcNow(), action.PackageId, action.Scripts)] });
    }

    /// <summary>
    /// Runs the held uninstallations that may run now and reports their results: one waiting for the maintenance window
    /// or for the lease of a coordinated group stays held. False if the central server rejected the credentials.
    /// </summary>
    private async Task<bool> RunUninstallsAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        foreach (var request in _state.Uninstalls ?? [])
        {
            // Software uninstalls in its software window, a Windows update in the maintenance window (Marc, 2026-11-14).
            var windows = request.PackageId is null ? EffectiveWindows : EffectiveSoftwareWindows;
            if (request.Kind == AgentActionKind.UninstallInWindow && !InstallationPlanner.IsInWindow(windows, _timeProvider.GetLocalNow().DateTime))
            {
                continue;
            }

            // In a coordinated group the uninstallation of an update and the restart after it need the lease, like an
            // installation; software is not coordinated (Marc, 2026-11-13 and 2026-11-14). A restart it needs waits for
            // a maintenance window and goes through the usual reboot path, which takes the lease then.
            if (request.PackageId is null)
            {
                var lease = await EnsureLeaseAsync(identity, cancellationToken).ConfigureAwait(false);
                if (lease is null)
                {
                    return false;
                }

                if (!lease.Value)
                {
                    // Tried again with the next cycle; the wait is logged with the reason.
                    break;
                }
            }

            var result = await UninstallAsync(request, cancellationToken).ConfigureAwait(false);
            Save(_state with
            {
                Uninstalls = [.. (_state.Uninstalls ?? []).Where(u => u.ActionId != request.ActionId)],
                ActionOutbox = [.. _state.ActionOutbox ?? [], new ActionOutcome(request.ActionId, result)],
            });
        }

        return await FlushActionOutboxAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the update, then restarts at once or in the next window if Windows needs it; never against the local veto.</summary>
    private async Task<AgentActionResult> UninstallAsync(UninstallRequest request, CancellationToken cancellationToken)
    {
        // The live status shows the uninstallation while it runs (rc.8 lab: nothing showed that it was busy).
        UninstallResult result;
        using (_activity?.Begin(AgentPhase.Uninstalling, request.UpdateId == Guid.Empty ? null : [request.UpdateId]))
        {
            // Software (removed assignment) goes to WinGet, an update to Windows Update or DISM.
            result = request.PackageId is { } packageId
                ? _softwareUninstaller is null
                    ? new UninstallResult(UninstallStatus.Failed, false, "This agent cannot uninstall software.")
                    : await _softwareUninstaller.UninstallAsync(packageId, request.Scripts, cancellationToken).ConfigureAwait(false)
                : await _uninstaller.UninstallAsync(request.UpdateId, request.KbArticle, cancellationToken).ConfigureAwait(false);
        }

        if (result.RebootRequired)
        {
            RememberInstallerRestart();
        }

        // The next inventory and search tell the central server what is installed now.
        _inventory.Reset();
        _updates.RequestRun();
        if (request.PackageId is null && _state.Lease is { } hold)
        {
            Save(_state with { Lease = hold with { InstallationSucceeded = hold.InstallationSucceeded && result.Status == UninstallStatus.Succeeded, AwaitingReboot = result.RebootRequired } });
        }

        if (result.Status != UninstallStatus.Succeeded)
        {
            return new AgentActionResult { Status = result.Status == UninstallStatus.Failed ? AgentActionStatus.Failed : AgentActionStatus.Rejected, Detail = result.Detail };
        }

        if (!result.RebootRequired)
        {
            return Succeeded(result.Detail);
        }

        if (!_options.Value.AllowReboot)
        {
            return Succeeded($"{result.Detail} Reboots are forbidden on this server (AllowReboot); the restart is left to an administrator.");
        }

        if (request.Kind == AgentActionKind.Uninstall)
        {
            if (!_restart.Schedule(_options.Value.RestartWarning))
            {
                return new AgentActionResult { Status = AgentActionStatus.Failed, Detail = $"{result.Detail} Windows did not accept the restart request." };
            }

            Save(_state with { LastRebootRequestAt = _timeProvider.GetUtcNow() });
            return Succeeded($"{result.Detail} The server restarts in 5 minutes.");
        }

        RequestRestart();
        return Succeeded($"{result.Detail} The restart waits for the next maintenance window.");
    }

    private static AgentActionResult Succeeded(string detail) => new() { Status = AgentActionStatus.Succeeded, Detail = detail };

    private async Task<bool> FlushActionOutboxAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        while (_state.ActionOutbox is [var outcome, ..])
        {
            if (!await _client.SendActionResultAsync(identity, outcome.ActionId, outcome.Result, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            Save(_state with { ActionOutbox = [.. _state.ActionOutbox.Skip(1)] });
        }

        return true;
    }

    internal IReadOnlyList<Release> PlanBatch()
    {
        var options = _options.Value;
        var missing = WithSoftware(WithoutAwaitingRestart(_updates.LastMissingUpdates));
        if (!options.Enabled || missing is null)
        {
            return [];
        }

        var attempts = _state.Attempts.ToDictionary(a => (a.UpdateId, a.Revision), a => a.AttemptedAt);
        var localNow = _timeProvider.GetLocalNow().DateTime;
        var inWindow = InstallationPlanner.IsInWindow(EffectiveWindows, localNow);
        var current = _releases.Current;
        if (current.SoftwareWindows is not { Count: > 0 } || !current.Releases.Any(r => r.Software is not null))
        {
            return InstallationPlanner.PlanBatch(current, _releases.InstallationsPaused, missing, attempts, _timeProvider.GetUtcNow(), options, inWindow);
        }

        // Software has windows of its own (Marc, 2026-11-13): Windows updates follow the maintenance windows, software its
        // software windows; one-time windows count for both.
        var inSoftwareWindow = InstallationPlanner.IsInWindow(EffectiveSoftwareWindows, localNow);
        var updates = InstallationPlanner.PlanBatch(current with { Releases = [.. current.Releases.Where(r => r.Software is null)] }, _releases.InstallationsPaused, missing, attempts, _timeProvider.GetUtcNow(), options, inWindow);
        var software = InstallationPlanner.PlanBatch(current with { Releases = [.. current.Releases.Where(r => r.Software is not null)] }, _releases.InstallationsPaused, missing, attempts, _timeProvider.GetUtcNow(), options, inSoftwareWindow);
        return [.. updates, .. software];
    }

    /// <summary>
    /// The missing updates without those installed successfully since the last start of Windows while a restart is
    /// pending: Windows searches them as missing until the restart, and installing them again in every window changes
    /// nothing (lab, LABVEX01, 2026-11-13: Windows refused the restart, the CU was installed again and again).
    /// </summary>
    internal IReadOnlySet<(Guid UpdateId, int Revision)>? WithoutAwaitingRestart(IReadOnlySet<(Guid UpdateId, int Revision)>? missing)
    {
        if (missing is null || !RebootPending)
        {
            return missing;
        }

        var awaiting = _state.Attempts.Where(a => a.Succeeded == true && a.AttemptedAt > _environment.LastBootAt).Select(a => (a.UpdateId, a.Revision)).ToHashSet();
        if (awaiting.Count == 0 || !missing.Overlaps(awaiting))
        {
            return missing;
        }

        var remaining = missing.Where(m => !awaiting.Contains(m)).ToHashSet();
        LogAwaitingRestart(missing.Count - remaining.Count);
        return remaining;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} installed updates wait for the restart and are not installed again.")]
    private partial void LogAwaitingRestart(int count);

    /// <summary>
    /// The updates the last search found missing plus the released software versions: the central server releases a
    /// software version only to servers whose inventory has an older one, and WinGet decides on the server.
    /// </summary>
    private IReadOnlySet<(Guid UpdateId, int Revision)>? WithSoftware(IReadOnlySet<(Guid UpdateId, int Revision)>? missing)
    {
        var software = _releases.Current.Releases.Where(r => r.Software is not null).Select(r => (r.UpdateId, r.Revision)).ToList();
        return software.Count == 0 || missing is null ? missing : missing.Concat(software).ToHashSet();
    }

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
        var installed = results.Where(r => r.ResultCode is InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors).Select(r => (r.UpdateId, r.Revision)).ToHashSet();
        var attempts = _state.Attempts.Where(a => !attempted.Contains((a.UpdateId, a.Revision)))
            .Concat(batch.Select(r => new InstallAttempt(r.UpdateId, r.Revision, started, r.Software is null && installed.Contains((r.UpdateId, r.Revision)))))
            .ToList();
        Save(_state with { Outbox = outbox, Attempts = attempts });
        if (report.RebootRequired)
        {
            RememberInstallerRestart();
        }

        // Installed state changed: refresh inventory and search with the next cycles.
        _inventory.Reset();
        _updates.RequestRun();

        var failed = results.Count(r => r.ResultCode is not (InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors));
        if (_state.InstallNow is { } now)
        {
            Save(_state with { InstallNow = now with { Succeeded = now.Succeeded + results.Count - failed, Failed = now.Failed + failed } });
        }

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
