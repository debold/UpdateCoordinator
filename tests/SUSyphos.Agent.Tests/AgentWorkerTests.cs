using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Trust;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class AgentWorkerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly FakeCentralServerClient _client = new();
    private readonly InMemoryIdentityStore _store = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeServerTrust _trust = new();
    private readonly FakeInventoryReporter _inventory = new();
    private readonly FakeReleaseSync _releases = new();
    private readonly FakeUpdateReporter _updates = new();
    private readonly FakeInstallationRunner _installation = new();
    private readonly FakeServiceRestarter _restarter = new();
    private readonly FakeProcessResources _process = new();
    private readonly SUSyphos.Agent.Status.ActivityTracker _activity = new(TimeProvider.System);
    private readonly FakeEnvironment _environment = new();

    [Fact]
    public async Task RunCycle_WithoutIdentity_RegistersStoresIdentityAndSendsHeartbeat()
    {
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _client.Registrations);
        Assert.Equal(1, _client.Heartbeats);
        Assert.Equal(_client.IssuedIdentity, _store.Identity);
        Assert.Equal("SRV01", _client.LastRegisterRequest?.HostName);
        Assert.Equal("1.2.3", _client.LastRegisterRequest?.AgentVersion);
    }

    [Fact]
    public async Task RunCycle_WithStoredIdentity_DoesNotRegisterAgain()
    {
        _store.Identity = new AgentIdentity(Guid.NewGuid(), "stored-secret");
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _client.Registrations);
        Assert.Equal(1, _client.Heartbeats);
    }

    [Fact]
    public async Task RunCycle_WhenCredentialsAreRejected_DeletesIdentityAndRegistersOnNextCycle()
    {
        _store.Identity = new AgentIdentity(Guid.NewGuid(), "revoked-secret");
        _client.OnHeartbeat = identity => identity == _client.IssuedIdentity ? new HeartbeatResponse(RegistrationState.Pending) : null;
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Null(_store.Identity);

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _client.Registrations);
        Assert.Equal(_client.IssuedIdentity, _store.Identity);
    }

    [Fact]
    public async Task Execute_WhenCentralServerFails_KeepsRunningAndRetriesWithBackoff()
    {
        var attempts = new System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>();
        _client.OnHeartbeat = _ =>
        {
            attempts.Enqueue(_time.GetUtcNow());
            throw new HttpRequestException("unreachable");
        };
        using var worker = CreateWorker();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await AdvanceUntilAsync(() => attempts.Count >= 3);

        // Exponential back-off: the gap after the second failure is at least twice the interval.
        var times = attempts.ToArray();
        Assert.True(times[1] - times[0] >= Interval);
        Assert.True(times[2] - times[1] >= 2 * Interval);
        Assert.False(worker.ExecuteTask!.IsCompleted);
        await worker.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(worker.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task Execute_LowersPriority_AndRestartsWhenMemoryStaysAboveTheLimit()
    {
        using var worker = CreateWorker();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _client.Heartbeats >= 1);
        Assert.True(_process.PriorityLowered);
        Assert.Equal(0, _restarter.Requests);

        _process.PrivateMemoryBytes = 500L * 1024 * 1024;
        await AdvanceUntilAsync(() => worker.ExecuteTask!.IsCompleted);

        Assert.Equal(1, _restarter.Requests);
        Assert.False(worker.ExecuteTask!.IsFaulted);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunCycle_WhenServerIsNotTrusted_SendsNothing()
    {
        _trust.Trusted = false;
        using var worker = CreateWorker();

        await Assert.ThrowsAsync<UntrustedServerException>(() => worker.RunCycleAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, _client.Registrations);
        Assert.Equal(0, _client.Heartbeats);
    }

    [Fact]
    public async Task RunCycle_AfterLocalTrustReset_ReportsResetOnce()
    {
        _trust.ResetNotReported = true;
        var reported = new List<bool>();
        _client.OnHeartbeatRequest = request => reported.Add(request.TrustReset);
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal([true, false], reported);
    }

    [Fact]
    public async Task Execute_WhenTlsHandshakeFails_RequestsTrustRefresh()
    {
        _client.OnHeartbeat = _ => throw new HttpRequestException("handshake", new System.Security.Authentication.AuthenticationException("certificate rejected"));
        using var worker = CreateWorker();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _trust.RefreshRequests >= 1);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(worker.ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task RunCycle_ReportsInventoryAfterHeartbeat_AndResetsItOnRegistration()
    {
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _inventory.Reports);
        Assert.Equal(1, _inventory.Resets);
    }

    [Fact]
    public async Task RunCycle_WhenInventoryCredentialsAreRejected_ForgetsIdentity()
    {
        _store.Identity = new AgentIdentity(Guid.NewGuid(), "stored-secret");
        _inventory.Accept = false;
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Null(_store.Identity);
    }

    [Fact]
    public async Task RunCycle_SendsWhenTheAnnouncedRestartHappens()
    {
        HeartbeatRequest? sent = null;
        _client.OnHeartbeatRequest = request => sent = request;
        var at = new DateTimeOffset(2026, 10, 25, 14, 23, 0, TimeSpan.Zero);
        _installation.RestartAnnouncedAt = at;
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(at, sent?.RestartAnnouncedAt);
    }

    [Fact]
    public void RestartAnnouncement_IsKeptDuringTheWarning_AndForgottenWhenCancelledOrOverdue()
    {
        var announcement = new SUSyphos.Agent.Installation.RestartAnnouncement(_time);
        Assert.Null(announcement.At);

        announcement.Announce(TimeSpan.FromMinutes(5));
        Assert.Equal(_time.GetUtcNow().AddMinutes(5), announcement.At);

        _time.Advance(TimeSpan.FromMinutes(5) + SUSyphos.Agent.Installation.RestartAnnouncement.Overdue);
        Assert.Null(announcement.At);

        announcement.Announce(TimeSpan.FromMinutes(5));
        announcement.Clear();
        Assert.Null(announcement.At);
    }

    [Fact]
    public async Task RunCycle_SendsOsVersionWithHeartbeat()
    {
        HeartbeatRequest? sent = null;
        _client.OnHeartbeatRequest = request => sent = request;
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("10.0.20348.2700", sent?.OsVersion);
    }

    [Fact]
    public async Task RunCycle_SendsLiveStatus_AndWhyAPendingRebootWaits()
    {
        HeartbeatRequest? sent = null;
        _client.OnHeartbeatRequest = request => sent = request;
        _installation.RebootWait = RebootWait.OutsideWindow;
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Null(sent?.Activity);
        Assert.Null(sent?.RebootWait);

        _environment.RebootPending = true;
        using (_activity.Begin(AgentPhase.Installing, [Guid.NewGuid()]))
        {
            await worker.RunCycleAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(AgentPhase.Installing, sent?.Activity?.Phase);
        Assert.Equal(RebootWait.OutsideWindow, sent?.RebootWait);
    }

    [Fact]
    public async Task RunCycle_ForPendingClient_SkipsUpdateRun()
    {
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _updates.Runs);
    }

    [Fact]
    public async Task RunCycle_ForApprovedClient_RunsUpdatesAndSyncsReleases()
    {
        _client.OnHeartbeat = _ => new HeartbeatResponse(RegistrationState.Approved, 7);
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _updates.Runs);
        Assert.Equal([7L], _releases.AnnouncedVersions);
    }

    [Fact]
    public async Task RunCycle_WhenUpdateReportIsRejected_ForgetsIdentity()
    {
        _store.Identity = new AgentIdentity(Guid.NewGuid(), "stored-secret");
        _client.OnHeartbeat = _ => new HeartbeatResponse(RegistrationState.Approved, 1);
        _updates.Accept = false;
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Null(_store.Identity);
    }

    [Fact]
    public async Task RunCycle_ForApprovedClient_RunsInstallationAfterSearch()
    {
        _client.OnHeartbeat = _ => new HeartbeatResponse(RegistrationState.Approved, 1);
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _installation.Runs);
    }

    [Fact]
    public async Task RunCycle_ForPendingClient_NeverInstalls()
    {
        using var worker = CreateWorker();

        await worker.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _installation.Runs);
    }

    [Fact]
    public async Task Execute_StopsCleanly()
    {
        using var worker = CreateWorker();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _client.Heartbeats >= 1);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.False(worker.ExecuteTask.IsFaulted);
    }

    private AgentWorker CreateWorker() => new(
        _client,
        _store,
        _environment,
        _trust,
        _inventory,
        _releases,
        _updates,
        _installation,
        new SUSyphos.Agent.Actions.ActionRunner(_client, _updates, _inventory, _restarter, new FakeRestart(), _installation, new FakeLogCollector(), new FakeAgentUninstaller(), Options.Create(new SUSyphos.Agent.Installation.InstallationOptions()), NullLogger<SUSyphos.Agent.Actions.ActionRunner>.Instance),
        AgentSelfUpdaterTests.CreateDisabled("1.2.3"),
        new ServerMover(_trust, new FakeTrustBindingFetcher(), new FakeMoveTargetProbe(), new FakeAgentAddressStore(), _restarter, Options.Create(new CentralServerOptions { BaseAddress = new Uri("https://central.example.local/") }), _time, NullLogger<ServerMover>.Instance),
        AutomaticUpdatesManagerTests.Create(new FakeSystemProbe(), new FakeRegistryWriter(new FakeSystemProbe())),
        MicrosoftUpdateManagerTests.Create(new FakeSystemProbe(), new FakeUpdateServiceRegistry(), new SUSyphos.Agent.Updates.UpdateServiceSelection()),
        new ResourceGuard(_process, _restarter, Options.Create(new ResourceOptions()), NullLogger<ResourceGuard>.Instance),
        _activity,
        Options.Create(new CentralServerOptions
        {
            BaseAddress = new Uri("https://central.example.local/"),
            HeartbeatInterval = Interval,
            MaxBackoff = TimeSpan.FromMinutes(5),
        }),
        _time,
        NullLogger<AgentWorker>.Instance);

    /// <summary>Moves fake time forward in small steps until the condition holds.</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
