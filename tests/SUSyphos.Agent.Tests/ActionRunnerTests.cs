using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using SUSyphos.Agent.Actions;
using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Installation;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class ActionRunnerTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");
    private static readonly HeartbeatResponse Announced = new(RegistrationState.Approved, 1, false, 2);

    private readonly FakeCentralServerClient _client = new();
    private readonly FakeUpdateReporter _updates = new();
    private readonly FakeInventoryReporter _inventory = new();
    private readonly FakeServiceRestarter _restarter = new();

    private readonly FakeRestart _systemRestart = new();
    private readonly FakeLogCollector _logs = new();
    private readonly FakeInstallationRunner _runner = new();
    private InstallationOptions _installation = new();

    private readonly FakeUninstaller _uninstaller = new();

    private ActionRunner Create() => new(_client, _updates, _inventory, _restarter, _systemRestart, _runner, _uninstaller, _logs, Microsoft.Extensions.Options.Options.Create(_installation), NullLogger<ActionRunner>.Instance);

    private static AgentAction Action(AgentActionKind kind) => new(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SearchAndResync_TriggerTheRegularSteps_AndReportSuccess()
    {
        var search = Action(AgentActionKind.SearchNow);
        var resync = Action(AgentActionKind.FullResync);
        _client.ActionList = new AgentActionList([search, resync]);

        Assert.True(await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken));

        Assert.Equal(2, _updates.RunRequests);
        Assert.Equal(1, _inventory.Resets);
        Assert.All(_client.ActionResults.Values, r => Assert.Equal(AgentActionStatus.Succeeded, r.Status));
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public async Task RestartService_ReportsFirstAndRestartsAfterAllResults()
    {
        var restart = Action(AgentActionKind.RestartService);
        var search = Action(AgentActionKind.SearchNow);
        _client.ActionList = new AgentActionList([restart, search]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(2, _client.ActionResults.Count);
        Assert.Equal(1, _restarter.Requests);
    }

    [Fact]
    public async Task RestartServer_SchedulesRestart_UnlessVetoed()
    {
        var allowed = Action(AgentActionKind.RestartServer);
        _client.ActionList = new AgentActionList([allowed]);
        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        _installation = new InstallationOptions { AllowReboot = false };
        var vetoed = Action(AgentActionKind.RestartServer);
        _client.ActionList = new AgentActionList([vetoed]);
        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(AgentActionStatus.Succeeded, _client.ActionResults[allowed.Id].Status);
        Assert.Equal(AgentActionStatus.Rejected, _client.ActionResults[vetoed.Id].Status);
        Assert.Equal([RebootPlanner.WarningPeriod], _systemRestart.Requests);
    }

    [Fact]
    public async Task RestartInWindow_IsHeldWithItsPlannedTime_AndCanBeCancelled()
    {
        _runner.PlannedRestartAt = new DateTimeOffset(2026, 10, 24, 19, 0, 0, TimeSpan.Zero);
        var request = Action(AgentActionKind.RestartServerInWindow);
        _client.ActionList = new AgentActionList([request]);
        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.True(_runner.RestartRequested);
        Assert.Empty(_systemRestart.Requests);
        Assert.Contains("2026-10-24 19:00 UTC", _client.ActionResults[request.Id].Detail, StringComparison.Ordinal);

        var cancel = Action(AgentActionKind.CancelRestart);
        _client.ActionList = new AgentActionList([cancel]);
        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.False(_runner.RestartRequested);
        Assert.Equal(AgentActionStatus.Succeeded, _client.ActionResults[cancel.Id].Status);
    }

    [Fact]
    public async Task RestartInWindow_IsRejectedByTheLocalVeto()
    {
        _installation = new InstallationOptions { AllowReboot = false };
        var request = Action(AgentActionKind.RestartServerInWindow);
        _client.ActionList = new AgentActionList([request]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(AgentActionStatus.Rejected, _client.ActionResults[request.Id].Status);
        Assert.False(_runner.RestartRequested);
    }

    [Theory]
    [InlineData(AgentActionKind.InstallNow, true, true, "right after")]
    [InlineData(AgentActionKind.InstallNowRestartInWindow, true, false, "next maintenance window")]
    [InlineData(AgentActionKind.InstallNow, false, true, "forbidden")]
    public async Task InstallNow_IsHandedToTheInstallation(AgentActionKind kind, bool allowReboot, bool restartNow, string detail)
    {
        _installation = new InstallationOptions { AllowReboot = allowReboot };
        var request = Action(kind);
        _client.ActionList = new AgentActionList([request]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(restartNow, _runner.InstallNowRestartNow);
        Assert.Equal(AgentActionStatus.Succeeded, _client.ActionResults[request.Id].Status);
        Assert.Contains(detail, _client.ActionResults[request.Id].Detail, StringComparison.Ordinal);
        Assert.Empty(_systemRestart.Requests);
    }

    [Theory]
    [InlineData(AgentActionKind.Uninstall, true, 1, false)]
    [InlineData(AgentActionKind.UninstallRestartInWindow, true, 0, true)]
    [InlineData(AgentActionKind.Uninstall, false, 0, false)]
    public async Task Uninstall_RemovesTheUpdate_AndRestartsAsAsked(AgentActionKind kind, bool allowReboot, int restarts, bool heldForWindow)
    {
        _installation = new InstallationOptions { AllowReboot = allowReboot };
        _uninstaller.Result = new SUSyphos.Agent.Installation.UninstallResult(SUSyphos.Agent.Installation.UninstallStatus.Succeeded, true, "Uninstalled with DISM.");
        var updateId = Guid.NewGuid();
        var request = new AgentAction(Guid.NewGuid(), kind, DateTimeOffset.UtcNow, updateId, 1, "5040434");
        _client.ActionList = new AgentActionList([request]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal([(updateId, (string?)"5040434")], _uninstaller.Calls);
        Assert.Equal(AgentActionStatus.Succeeded, _client.ActionResults[request.Id].Status);
        Assert.Equal(restarts, _systemRestart.Requests.Count);
        Assert.Equal(heldForWindow, _runner.RestartRequested);
        Assert.Equal(1, _inventory.Resets);
        Assert.Equal(1, _updates.RunRequests);
    }

    [Fact]
    public async Task Uninstall_ThatFails_OrNamesNoUpdate_IsReportedSo()
    {
        _uninstaller.Result = new SUSyphos.Agent.Installation.UninstallResult(SUSyphos.Agent.Installation.UninstallStatus.Failed, false, "DISM could not remove it.");
        var failing = new AgentAction(Guid.NewGuid(), AgentActionKind.Uninstall, DateTimeOffset.UtcNow, Guid.NewGuid(), 1, "5040434");
        var nameless = Action(AgentActionKind.Uninstall);
        _client.ActionList = new AgentActionList([failing, nameless]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(AgentActionStatus.Failed, _client.ActionResults[failing.Id].Status);
        Assert.Equal(AgentActionStatus.Rejected, _client.ActionResults[nameless.Id].Status);
        Assert.Single(_uninstaller.Calls);
        Assert.Empty(_systemRestart.Requests);
    }

    [Fact]
    public async Task RestartServerRefusedByWindows_IsReportedAsFailed()
    {
        var action = Action(AgentActionKind.RestartServer);
        _client.ActionList = new AgentActionList([action]);
        _systemRestart.Accept = false;

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(AgentActionStatus.Failed, _client.ActionResults[action.Id].Status);
    }

    [Fact]
    public async Task UploadLogs_UploadsArchiveThenReports()
    {
        var action = Action(AgentActionKind.UploadLogs);
        _client.ActionList = new AgentActionList([action]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.True(_client.UploadedLogs.ContainsKey(action.Id));
        Assert.Equal(AgentActionStatus.Succeeded, _client.ActionResults[action.Id].Status);
    }

    [Fact]
    public async Task UnreadableLogs_AreReportedAsFailed()
    {
        var action = Action(AgentActionKind.UploadLogs);
        _client.ActionList = new AgentActionList([action]);
        _logs.Failure = new IOException("locked");

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Empty(_client.UploadedLogs);
        Assert.Equal(AgentActionStatus.Failed, _client.ActionResults[action.Id].Status);
    }

    [Fact]
    public async Task UnknownAction_IsRejected()
    {
        var unknown = Action((AgentActionKind)99);
        _client.ActionList = new AgentActionList([unknown]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(AgentActionStatus.Rejected, _client.ActionResults[unknown.Id].Status);
    }

    [Fact]
    public async Task NothingAnnouncedOrNotApproved_FetchesNothing()
    {
        _client.ActionList = new AgentActionList([Action(AgentActionKind.SearchNow)]);

        await Create().RunAnnouncedAsync(Identity, new HeartbeatResponse(RegistrationState.Approved, 1), TestContext.Current.CancellationToken);
        await Create().RunAnnouncedAsync(Identity, new HeartbeatResponse(RegistrationState.Pending, null, false, 3), TestContext.Current.CancellationToken);

        Assert.Empty(_client.ActionResults);
        Assert.Equal(0, _updates.RunRequests);
    }

    [Fact]
    public async Task RejectedCredentials_ReturnFalseAndDoNotRestart()
    {
        _client.ActionList = new AgentActionList([Action(AgentActionKind.RestartService)]);
        _client.AcceptActionResults = false;

        Assert.False(await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken));
        Assert.Equal(0, _restarter.Requests);

        _client.ActionList = null;
        Assert.False(await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken));
    }
}

public sealed class ActionResponseTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    [Theory]
    [InlineData("{\"actions\":null}")]
    [InlineData("{\"actions\":[null]}")]
    [InlineData("{\"actions\":[{\"id\":\"00000000-0000-0000-0000-000000000000\",\"kind\":\"SearchNow\",\"requestedAt\":\"2026-10-19T00:00:00Z\"}]}")]
    [InlineData("null")]
    public async Task InvalidActionList_IsRejected(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(HttpStatusCode.OK, json).GetActionsAsync(Identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TooManyActions_AreRejected()
    {
        var items = string.Join(',', Enumerable.Range(0, AgentActionList.MaxActions + 1).Select(_ => $"{{\"id\":\"{Guid.NewGuid()}\",\"kind\":\"SearchNow\",\"requestedAt\":\"2026-10-19T00:00:00Z\"}}"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Create(HttpStatusCode.OK, $"{{\"actions\":[{items}]}}").GetActionsAsync(Identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredAction_ResultIsTreatedAsDelivered()
    {
        Assert.True(await Create(HttpStatusCode.NotFound, "").SendActionResultAsync(Identity, Guid.NewGuid(), new AgentActionResult { Status = AgentActionStatus.Succeeded }, TestContext.Current.CancellationToken));
        Assert.False(await Create(HttpStatusCode.Unauthorized, "").SendActionResultAsync(Identity, Guid.NewGuid(), new AgentActionResult { Status = AgentActionStatus.Succeeded }, TestContext.Current.CancellationToken));
    }

    private static CentralServerClient Create(HttpStatusCode status, string json) =>
        new(new HttpClient(new StubHandler(status, json)) { BaseAddress = new Uri("https://central.example.local/") });

    private sealed class StubHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
