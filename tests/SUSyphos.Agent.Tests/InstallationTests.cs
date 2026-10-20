using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Installation;
using SUSyphos.Agent.Updates;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class MaintenanceWindowTests
{
    private static readonly IReadOnlyList<MaintenanceWindow> Default = MaintenanceWindow.SystemDefault;

    [Theory]
    [InlineData("2026-10-19T19:00:00", true)] // Monday evening, start of window
    [InlineData("2026-10-19T18:59:59", false)] // Monday just before
    [InlineData("2026-10-20T04:59:59", true)] // Tuesday early morning belongs to Monday's window
    [InlineData("2026-10-20T05:00:00", false)] // Tuesday 05:00 ends it
    [InlineData("2026-10-19T03:00:00", false)] // Monday early morning: Sunday's window ends at midnight
    [InlineData("2026-10-21T12:00:00", false)] // Wednesday noon
    [InlineData("2026-10-24T12:00:00", true)] // Saturday noon
    [InlineData("2026-10-25T23:59:59", true)] // Sunday late
    [InlineData("2026-10-24T03:00:00", true)] // Saturday early (Friday window and Saturday all day)
    public void DefaultWindows(string localTime, bool expected)
    {
        Assert.Equal(expected, InstallationPlanner.IsInWindow(Default, DateTime.Parse(localTime, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void CustomWindowWithinDay()
    {
        var windows = new[] { new MaintenanceWindow { Days = [DayOfWeek.Wednesday], Start = TimeSpan.FromHours(2), End = TimeSpan.FromHours(4) } };

        Assert.True(InstallationPlanner.IsInWindow(windows, new DateTime(2026, 10, 21, 3, 0, 0)));
        Assert.False(InstallationPlanner.IsInWindow(windows, new DateTime(2026, 10, 21, 4, 0, 0)));
        Assert.False(InstallationPlanner.IsInWindow(windows, new DateTime(2026, 10, 22, 3, 0, 0)));
    }

    [Fact]
    public void NoWindows_NeverMatch()
    {
        Assert.False(InstallationPlanner.IsInWindow([], new DateTime(2026, 10, 24, 12, 0, 0)));
    }
}

public sealed class InstallationOptionsTests
{
    [Fact]
    public void Defaults_AreValidAndUseSystemWindows()
    {
        var options = new InstallationOptions();

        Assert.Empty(Validate(options));
        Assert.Same(MaintenanceWindow.SystemDefault, options.LocalWindows);
    }

    [Theory]
    [InlineData("", "19:00:00", "05:00:00")]
    [InlineData("Monday", "-01:00:00", "05:00:00")]
    [InlineData("Monday", "1.00:00:00", "05:00:00")]
    [InlineData("Monday", "19:00:00", "00:00:00")]
    [InlineData("Monday", "19:00:00", "1.00:00:01")]
    public void InvalidWindow_IsRejected(string day, string start, string end)
    {
        var options = new InstallationOptions
        {
            Windows =
            [
                new MaintenanceWindowRule
                {
                    Days = day.Length == 0 ? [] : [Enum.Parse<DayOfWeek>(day)],
                    Start = TimeSpan.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
                    End = TimeSpan.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
                },
            ],
        };

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void UndefinedWeekday_IsRejected()
    {
        var options = new InstallationOptions { Windows = [new MaintenanceWindowRule { Days = [(DayOfWeek)9], Start = TimeSpan.Zero, End = TimeSpan.FromHours(1) }] };

        Assert.NotEmpty(Validate(options));
    }

    [Theory]
    [InlineData("05:00:00", "06:00:00")]
    [InlineData("00:30:00", "00:30:00")]
    public void ToleranceOrRetryOutOfRange_IsRejected(string tolerance, string retry)
    {
        var options = new InstallationOptions
        {
            PullForwardTolerance = TimeSpan.Parse(tolerance, System.Globalization.CultureInfo.InvariantCulture),
            RetryDelay = TimeSpan.Parse(retry, System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.NotEmpty(Validate(options));
    }

    private static List<ValidationResult> Validate(InstallationOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}

public sealed class BatchPlanningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly InstallationOptions Options = new();

    private readonly Release _due = new(Guid.NewGuid(), 1, Now.AddHours(-1));
    private readonly Release _soon = new(Guid.NewGuid(), 1, Now.AddMinutes(20));
    private readonly Release _later = new(Guid.NewGuid(), 1, Now.AddHours(2));

    [Fact]
    public void DueRelease_IsInstalledWithPullForward()
    {
        var batch = Plan(_due, _soon, _later);

        Assert.Equal([_due, _soon], batch);
    }

    [Fact]
    public void NothingDue_InstallsNothingEvenIfSoonDue()
    {
        Assert.Empty(Plan(_soon, _later));
    }

    [Fact]
    public void Pause_StopsInstallations()
    {
        Assert.Empty(Plan([_due], paused: true));
    }

    [Fact]
    public void AlreadyInstalledRelease_IsSkipped()
    {
        Assert.Empty(Plan([_due], missing: []));
    }

    [Fact]
    public void RecentlyAttemptedRelease_IsNotRetried()
    {
        var attempts = new Dictionary<(Guid, int), DateTimeOffset> { [(_due.UpdateId, _due.Revision)] = Now.AddHours(-1) };

        Assert.Empty(Plan([_due], attempts: attempts));
    }

    [Fact]
    public void AttemptOlderThanRetryDelay_IsRetried()
    {
        var attempts = new Dictionary<(Guid, int), DateTimeOffset> { [(_due.UpdateId, _due.Revision)] = Now.AddHours(-7) };

        Assert.Equal([_due], Plan([_due], attempts: attempts));
    }

    [Fact]
    public void OtherRevision_IsNotInstalled()
    {
        var missingOtherRevision = new HashSet<(Guid, int)> { (_due.UpdateId, 2) };

        Assert.Empty(Plan([_due], missing: missingOtherRevision));
    }

    private static IReadOnlyList<Release> Plan(params Release[] releases) => Plan(releases, paused: false);

    private static IReadOnlyList<Release> Plan(
        Release[] releases,
        bool paused = false,
        HashSet<(Guid, int)>? missing = null,
        Dictionary<(Guid, int), DateTimeOffset>? attempts = null) =>
        InstallationPlanner.PlanBatch(
            new ReleaseList(1, releases),
            paused,
            missing ?? releases.Select(r => (r.UpdateId, r.Revision)).ToHashSet(),
            attempts ?? [],
            Now,
            Options);
}

public sealed class InstallationRunnerTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    // Saturday noon UTC: inside the default window.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeReleaseSync _releases = new();
    private readonly FakeUpdateReporter _updates = new();
    private readonly FakeInventoryReporter _inventory = new();
    private readonly FakeInstaller _installer = new();
    private readonly FakeCentralServerClient _client = new();
    private readonly InMemoryInstallationStore _store = new();
    private readonly FakeSystemProbe _probe = new();
    private readonly FakeDiskSpace _disk = new();
    private readonly FakeEnvironment _environment = new();
    private readonly FakeRestart _restart = new();
    private readonly Release _release;

    public InstallationRunnerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _probe.Values[(@"SYSTEM\CurrentControlSet\Services\wuauserv", "Start")] = "3";
        _release = new Release(Guid.NewGuid(), 1, _time.GetUtcNow().AddHours(-1));
        _releases.Current = new ReleaseList(1, [_release]);
        _updates.LastMissingUpdates = new HashSet<(Guid, int)> { (_release.UpdateId, _release.Revision) };
    }

    [Fact]
    public async Task DueRelease_IsInstalledAndReported()
    {
        Assert.True(await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        Assert.Equal([_release], Assert.Single(_installer.Batches));
        var report = Assert.Single(_client.InstallReports);
        Assert.Equal(_release.UpdateId, Assert.Single(report.Results).UpdateId);
        Assert.True(report.RebootRequired);
        Assert.Empty(_store.State.Outbox);
        Assert.Single(_store.State.Attempts);
        Assert.Equal(1, _inventory.Resets);
        Assert.Equal(1, _updates.RunRequests);
    }

    [Fact]
    public async Task OutsideWindow_NothingIsInstalled()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)); // next Wednesday noon

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Empty(_installer.Batches);
    }

    [Fact]
    public async Task CentralWindows_TakePrecedenceOverLocalWindows()
    {
        // Saturday noon is inside the local default, but the central window only allows Wednesday noon.
        _releases.Current = new ReleaseList(1, [_release], [new MaintenanceWindow { Days = [DayOfWeek.Wednesday], Start = TimeSpan.FromHours(11), End = TimeSpan.FromHours(13) }]);

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Empty(_installer.Batches);

        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Single(_installer.Batches);
    }

    [Fact]
    public async Task EmptyCentralWindows_FallBackToLocalWindows()
    {
        _releases.Current = new ReleaseList(1, [_release], []);

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_installer.Batches);
    }

    [Fact]
    public async Task WithoutSearchResult_NothingIsInstalled()
    {
        _updates.LastMissingUpdates = null;

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Empty(_installer.Batches);
    }

    [Fact]
    public async Task Disabled_NothingIsInstalled()
    {
        await Create(new InstallationOptions { Enabled = false }).RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Empty(_installer.Batches);
    }

    [Fact]
    public async Task FailedHealthCheck_PreventsInstallation()
    {
        _disk.SystemDriveFreeBytes = 1;

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Empty(_installer.Batches);
    }

    [Fact]
    public async Task UndeliveredReport_StaysBufferedAndIsSentLater()
    {
        _client.OnInstallReport = _ => throw new HttpRequestException("unreachable");
        var runner = Create();

        await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken));
        Assert.Single(_store.State.Outbox);

        _client.OnInstallReport = _ => true;
        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_client.InstallReports);
        Assert.Empty(_store.State.Outbox);
        Assert.Single(_installer.Batches);
    }

    [Fact]
    public async Task BufferedReport_SurvivesRestart()
    {
        _client.OnInstallReport = _ => throw new HttpRequestException("unreachable");
        await Assert.ThrowsAsync<HttpRequestException>(() => Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        // New runner instance = service restart; the attempt is remembered, the report is delivered.
        _client.OnInstallReport = _ => true;
        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_client.InstallReports);
        Assert.Single(_installer.Batches);
    }

    [Fact]
    public async Task RejectedCredentials_ReturnFalseAndKeepReport()
    {
        _client.OnInstallReport = _ => false;

        Assert.False(await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        Assert.Single(_store.State.Outbox);
    }

    [Fact]
    public async Task Outbox_IsCapped()
    {
        _store.State = new InstallationState(
            [.. Enumerable.Range(0, InstallationRunner.MaxOutbox).Select(_ => Report())],
            []);
        _client.OnInstallReport = _ => throw new HttpRequestException("unreachable");
        var runner = Create();

        await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        Assert.Equal(InstallationRunner.MaxOutbox, _store.State.Outbox.Count);
    }

    [Fact]
    public async Task PendingRebootAllowedByPolicy_RestartsOnceWithWarning()
    {
        _releases.Current = new ReleaseList(1, [], null, RebootPolicy.InsideMaintenanceWindow);
        _environment.RebootPending = true;
        var runner = Create();

        Assert.True(await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken));
        Assert.True(await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        Assert.Equal([RebootPlanner.WarningPeriod], _restart.Requests);
        Assert.Equal(_time.GetUtcNow(), _store.State.LastRebootRequestAt);
    }

    [Fact]
    public async Task RebootWait_TellsWhyAPendingRebootWaits()
    {
        _environment.RebootPending = true;
        var runner = Create();

        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Equal(RebootWait.NotAllowedByPolicy, runner.RebootWait);

        _releases.Current = new ReleaseList(1, [], null, RebootPolicy.InsideMaintenanceWindow);
        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Equal(RebootWait.Restarting, runner.RebootWait);

        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Equal(RebootWait.RecentlyRequested, runner.RebootWait);

        _environment.RebootPending = false;
        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Null(runner.RebootWait);
    }

    [Fact]
    public void EveryRebootDecision_ButNotPending_HasItsOwnReason()
    {
        var reasons = Enum.GetValues<RebootDecision>().Where(d => d != RebootDecision.NotPending).Select(InstallationRunner.ToRebootWait).ToList();

        Assert.DoesNotContain(null, reasons);
        Assert.Equal(reasons.Count, reasons.Distinct().Count());
        Assert.Null(InstallationRunner.ToRebootWait(RebootDecision.NotPending));
    }

    [Fact]
    public async Task RestartRequest_SurvivesServiceRestart()
    {
        _releases.Current = new ReleaseList(1, [], null, RebootPolicy.InsideMaintenanceWindow);
        _environment.RebootPending = true;
        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        // A new runner (service restart) loads the state and does not request again within the cooldown.
        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_restart.Requests);
    }

    [Fact]
    public async Task RefusedRestart_IsNotRepeatedInALoop()
    {
        _releases.Current = new ReleaseList(1, [], null, RebootPolicy.InsideMaintenanceWindow);
        _environment.RebootPending = true;
        _restart.Accept = false;
        var runner = Create();

        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_restart.Requests);
    }

    [Fact]
    public async Task WithoutPolicyOrWithLocalVeto_NeverRestarts()
    {
        _environment.RebootPending = true;
        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        _releases.Current = new ReleaseList(1, [], null, RebootPolicy.InsideMaintenanceWindow);
        await Create(new InstallationOptions { AllowReboot = false }).RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Empty(_restart.Requests);
    }

    [Fact]
    public async Task InstallationNeedingReboot_RestartsInTheSameCycle()
    {
        _releases.Current = new ReleaseList(1, [_release], null, RebootPolicy.InsideMaintenanceWindow);
        _environment.RebootPending = true;

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_installer.Batches);
        Assert.Single(_client.InstallReports);
        Assert.Single(_restart.Requests);
    }

    [Fact]
    public async Task CoordinatedServer_WithoutFreeLease_DoesNotInstall()
    {
        _releases.Current = new ReleaseList(1, [_release], null, null, RequiresLease: true);
        _client.LeaseAnswer = new LeaseResponse(false, null, "All leases of group DCs are taken.");

        Assert.True(await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        Assert.Empty(_installer.Batches);
        Assert.Equal(1, _client.LeaseRequests);
        Assert.Null(_store.State.Lease);
    }

    [Fact]
    public async Task CoordinatedServer_InstallsWithLease_AndReleasesIt()
    {
        _releases.Current = new ReleaseList(1, [_release], null, null, RequiresLease: true);

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_installer.Batches);
        Assert.Equal([true], _client.LeaseReleases);
        Assert.Null(_store.State.Lease);
    }

    [Fact]
    public async Task LeaseIsKeptForTheReboot_AndReleasedAfterTheServerCameBack()
    {
        _releases.Current = new ReleaseList(1, [_release], null, RebootPolicy.InsideMaintenanceWindow, RequiresLease: true);
        _environment.RebootPending = true;
        var runner = Create();

        await runner.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Single(_restart.Requests);
        Assert.Empty(_client.LeaseReleases);
        Assert.True(_store.State.Lease!.AwaitingReboot);

        // After the restart: booted after the lease was granted, nothing pending any more.
        _environment.RebootPending = false;
        _environment.LastBootAt = _time.GetUtcNow().AddMinutes(10);
        _time.Advance(TimeSpan.FromMinutes(20));
        _updates.LastMissingUpdates = new HashSet<(Guid, int)>();
        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal([true], _client.LeaseReleases);
        Assert.Null(_store.State.Lease);
    }

    [Fact]
    public async Task WithoutRebootPermission_LeaseIsReleasedRightAfterInstallation()
    {
        _releases.Current = new ReleaseList(1, [_release], null, RebootPolicy.Never, RequiresLease: true);
        _environment.RebootPending = true;

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Empty(_restart.Requests);
        Assert.Equal([true], _client.LeaseReleases);
    }

    [Fact]
    public async Task FailedInstallation_ReleasesLeaseAsFailed()
    {
        _releases.Current = new ReleaseList(1, [_release], null, null, RequiresLease: true);
        _installer.ResultCode = InstallResultCode.Failed;

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal([false], _client.LeaseReleases);
    }

    private static InstallReport Report() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
        Results = [new InstallResult { UpdateId = Guid.NewGuid(), Revision = 1, ResultCode = InstallResultCode.Succeeded }],
    };

    private InstallationRunner Create(InstallationOptions? options = null)
    {
        var preFlight = new PreFlightCheck(_probe, _disk, Microsoft.Extensions.Options.Options.Create(new UpdateOptions()));
        return new InstallationRunner(
            _releases,
            _updates,
            _inventory,
            _installer,
            preFlight,
            _client,
            _store,
            _environment,
            _restart,
            Microsoft.Extensions.Options.Options.Create(options ?? new InstallationOptions()),
            _time,
            NullLogger<InstallationRunner>.Instance);
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public List<IReadOnlyList<Release>> Batches { get; } = [];

        public InstallResultCode ResultCode { get; set; } = InstallResultCode.Succeeded;

        public Task<IReadOnlyList<InstallResult>> InstallAsync(IReadOnlyList<Release> batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.FromResult<IReadOnlyList<InstallResult>>(
                [.. batch.Select(r => new InstallResult { UpdateId = r.UpdateId, Revision = r.Revision, ResultCode = ResultCode, RebootRequired = true })]);
        }
    }

    private sealed class InMemoryInstallationStore : IInstallationStateStore
    {
        public InstallationState State { get; set; } = InstallationState.Empty;

        public InstallationState Load() => State;

        public void Save(InstallationState state) => State = state;
    }
}
