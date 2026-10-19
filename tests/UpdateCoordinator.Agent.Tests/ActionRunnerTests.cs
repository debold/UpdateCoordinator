using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using UpdateCoordinator.Agent.Actions;
using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Tests;

public sealed class ActionRunnerTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");
    private static readonly HeartbeatResponse Announced = new(RegistrationState.Approved, 1, false, 2);

    private readonly FakeCentralServerClient _client = new();
    private readonly FakeUpdateReporter _updates = new();
    private readonly FakeInventoryReporter _inventory = new();
    private readonly FakeServiceRestarter _restarter = new();

    private ActionRunner Create() => new(_client, _updates, _inventory, _restarter, NullLogger<ActionRunner>.Instance);

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
