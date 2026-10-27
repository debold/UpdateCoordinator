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

    private readonly FakeAgentUninstaller _uninstaller = new();

    private ActionRunner Create() => new(_client, _updates, _inventory, _restarter, _systemRestart, _runner, _logs, _uninstaller, Microsoft.Extensions.Options.Options.Create(_installation), NullLogger<ActionRunner>.Instance);

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
    [InlineData(AgentActionKind.Uninstall)]
    [InlineData(AgentActionKind.UninstallRestartInWindow)]
    [InlineData(AgentActionKind.UninstallInWindow)]
    public async Task Uninstall_IsHandedToTheInstallationStep_WhichReportsTheResultLater(AgentActionKind kind)
    {
        var request = new AgentAction(Guid.NewGuid(), kind, DateTimeOffset.UtcNow, Guid.NewGuid(), 1, "5040434");
        _client.ActionList = new AgentActionList([request]);

        Assert.True(await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken));

        Assert.Equal([request], _runner.Uninstalls);
        Assert.False(_client.ActionResults.ContainsKey(request.Id));
        Assert.Empty(_systemRestart.Requests);
    }

    [Fact]
    public async Task Uninstall_ThatNamesNoUpdate_IsRejectedAtOnce()
    {
        var nameless = Action(AgentActionKind.Uninstall);
        _client.ActionList = new AgentActionList([nameless]);

        await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken);

        Assert.Equal(AgentActionStatus.Rejected, _client.ActionResults[nameless.Id].Status);
        Assert.Empty(_runner.Uninstalls);
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
    [Fact]
    public async Task UninstallAgent_IsConfirmedFirst_ThenTheOwnPackageIsUninstalled()
    {
        var uninstall = Action(AgentActionKind.UninstallAgent);
        _client.ActionList = new AgentActionList([uninstall]);

        Assert.True(await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken));

        Assert.Equal(AgentActionStatus.Succeeded, _client.ActionResults[uninstall.Id].Status);
        Assert.Equal(["{11111111-2222-3333-4444-555555555555}"], _uninstaller.Started);
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public async Task UninstallAgent_WithoutItsPackage_IsRejected()
    {
        _uninstaller.ProductCode = null;
        var uninstall = Action(AgentActionKind.UninstallAgent);
        _client.ActionList = new AgentActionList([uninstall]);

        Assert.True(await Create().RunAnnouncedAsync(Identity, Announced, TestContext.Current.CancellationToken));

        Assert.Equal(AgentActionStatus.Rejected, _client.ActionResults[uninstall.Id].Status);
        Assert.Empty(_uninstaller.Started);
    }

    [Fact]
    public void PackedCodes_AreWindowsInstallerForm_AndTheUpgradeCodeMatchesThePackage()
    {
        // The agent package: {F94F20CA-E2D1-4F0C-8D0C-4D810C149471} is stored as AC02F49F1D2EC0F4D8C0D418C0414917.
        Assert.Equal("AC02F49F1D2EC0F4D8C0D418C0414917", SUSyphos.Agent.Actions.MsiAgentUninstaller.Pack(Guid.Parse(SUSyphos.Agent.Actions.MsiAgentUninstaller.UpgradeCode)));
        var guid = Guid.Parse("12345678-9ABC-DEF0-1234-56789ABCDEF0");
        Assert.Equal("87654321CBA90FED21436587A9CBED0F", SUSyphos.Agent.Actions.MsiAgentUninstaller.Pack(guid));
        Assert.Equal(guid, SUSyphos.Agent.Actions.MsiAgentUninstaller.Unpack("87654321CBA90FED21436587A9CBED0F"));
        Assert.Null(SUSyphos.Agent.Actions.MsiAgentUninstaller.Unpack("not a code"));

        var package = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Agent", "Package.wxs"));
        Assert.Contains($"UpgradeCode=\"{SUSyphos.Agent.Actions.MsiAgentUninstaller.UpgradeCode}\"", package, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SUSyphos.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
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
