using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class LiveStatusTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Activity_IsStored_ShownOnTheServerPage_AndClearedWhenFinished()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.ReportAsync(client, RolloutScenario.Update("Security Updates"));
        var updateId = await scenario.Factory.WithDbAsync(db => db.Updates.Select(u => u.UpdateId).SingleAsync(TestContext.Current.CancellationToken));

        using (var response = await SendActivityAsync(scenario, client, new AgentActivity { Phase = AgentPhase.Downloading, Percent = 45, UpdateIds = [updateId], StartedAt = DateTimeOffset.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var stored = await scenario.Factory.WithDbAsync(db => db.Clients.AsNoTracking().SingleAsync(c => c.Id == client.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(AgentPhase.Downloading, stored.ActivityPhase);
        Assert.Equal(45, stored.ActivityPercent);
        Assert.Equal([updateId], stored.ActivityUpdateIds);
        Assert.Equal(ServerHealthState.Updating, ServerHealth.Evaluate(stored, false, DateTime.UtcNow));

        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var page = await (await reader.GetAsync($"/servers/{client.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("pill pend\">Running", page, StringComparison.Ordinal);
        Assert.Contains("http-equiv=\"refresh\"", page, StringComparison.Ordinal);
        Assert.Contains("45 %", page, StringComparison.Ordinal);
        Assert.Contains($"/updates/{updateId}", page, StringComparison.Ordinal);

        // Only the tabs with live state reload; settings and other tabs never lose what is opened or typed.
        var configuration = await (await reader.GetAsync($"/servers/{client.ClientId}?tab=configuration")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("susyphos-refresh", configuration, StringComparison.Ordinal);
        Assert.Contains("susyphos-refresh", page, StringComparison.Ordinal);

        using (var finished = await SendActivityAsync(scenario, client, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, finished.StatusCode);
        }

        page = await (await reader.GetAsync($"/servers/{client.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("pill pend\">Running", page, StringComparison.Ordinal);
        Assert.DoesNotContain("http-equiv=\"refresh\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_FromPendingClient_IsAccepted()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);

        using var response = await PostAsync(http, credentials, new AgentActivityReport { Activity = new AgentActivity { Phase = AgentPhase.Inventory, StartedAt = DateTimeOffset.UtcNow } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Activity_WithoutCredentials_IsUnauthorized()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync(new Uri(ApiRoutes.AgentActivity, UriKind.Relative), new AgentActivityReport(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(RegistrationState.Rejected)]
    [InlineData(RegistrationState.Blocked)]
    public async Task Activity_InStateWithoutPermission_IsForbidden(RegistrationState state)
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);
        await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, state), TestContext.Current.CancellationToken));

        using var response = await PostAsync(http, credentials, new AgentActivityReport());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"activity\":{\"phase\":\"Installing\",\"percent\":101,\"startedAt\":\"2026-10-20T10:00:00Z\"}}")]
    [InlineData("{\"activity\":{\"phase\":\"Installing\",\"percent\":-1,\"startedAt\":\"2026-10-20T10:00:00Z\"}}")]
    [InlineData("{\"activity\":{\"phase\":\"Formatting\",\"startedAt\":\"2026-10-20T10:00:00Z\"}}")]
    [InlineData("{\"activity\":{\"phase\":\"Installing\",\"updateIds\":[\"not-a-guid\"],\"startedAt\":\"2026-10-20T10:00:00Z\"}}")]
    public async Task Activity_WithInvalidBody_ReturnsBadRequest(string json)
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentActivity, UriKind.Relative)) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activity_WithTooManyUpdates_ReturnsBadRequest()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);

        using var response = await PostAsync(http, credentials, new AgentActivityReport
        {
            Activity = new AgentActivity { Phase = AgentPhase.Installing, UpdateIds = [.. Enumerable.Range(0, AgentActivity.MaxUpdates + 1).Select(_ => Guid.NewGuid())], StartedAt = DateTimeOffset.UtcNow },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_StoresRebootWait_OnlyWhileARebootIsPending()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        await HeartbeatAsync(scenario, client, new HeartbeatRequest { AgentVersion = "0.7.0", RebootPending = true, RebootWait = RebootWait.NotAllowedByPolicy });
        Assert.Equal(RebootWait.NotAllowedByPolicy, await RebootWaitAsync(scenario, client));

        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var page = await (await reader.GetAsync($"/servers/{client.ClientId}?tab=system")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("the reboot policy is &quot;Never&quot;", page, StringComparison.Ordinal);

        await HeartbeatAsync(scenario, client, new HeartbeatRequest { AgentVersion = "0.7.0", RebootPending = false, RebootWait = RebootWait.OutsideWindow });
        Assert.Null(await RebootWaitAsync(scenario, client));
    }

    [Fact]
    public async Task Heartbeat_CarriesTheActivity_AndAnUnknownRebootWaitIsIgnored()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        await HeartbeatAsync(scenario, client, new HeartbeatRequest
        {
            AgentVersion = "0.7.0",
            RebootPending = true,
            RebootWait = (RebootWait)99,
            Activity = new AgentActivity { Phase = AgentPhase.Searching, StartedAt = DateTimeOffset.UtcNow.AddYears(5) },
        });

        var stored = await scenario.Factory.WithDbAsync(db => db.Clients.AsNoTracking().SingleAsync(c => c.Id == client.ClientId, TestContext.Current.CancellationToken));
        Assert.Null(stored.RebootWait);
        Assert.Equal(AgentPhase.Searching, stored.ActivityPhase);
        Assert.True(stored.ActivityStartedAtUtc <= DateTime.UtcNow.AddMinutes(1), "A start in the future is replaced by the time of receipt.");

        // Searching is no installation: the pending reboot stays the status.
        Assert.Equal(ServerHealthState.RebootPending, ServerHealth.Evaluate(stored, false, DateTime.UtcNow));
    }

    [Fact]
    public void StaleActivity_IsNotCurrent()
    {
        var now = DateTime.UtcNow;
        var client = new ClientEntity
        {
            SecretHash = [],
            HostName = "SRV01",
            OsVersion = "10.0.20348.1",
            AgentVersion = "0.7.0",
            State = RegistrationState.Approved,
            LastHeartbeatAtUtc = now,
            RebootPending = true,
            ActivityPhase = AgentPhase.Installing,
            ActivityPercent = 10,
            ActivityReportedAtUtc = now - LiveStatus.StaleAfter - TimeSpan.FromSeconds(1),
        };

        Assert.Null(LiveStatus.CurrentPhase(client, now));
        Assert.Equal("Idle", LiveStatus.Describe(client, now));
        Assert.Equal(ServerHealthState.RebootPending, ServerHealth.Evaluate(client, false, now));

        client.ActivityReportedAtUtc = now;
        Assert.Equal("Installing 10 %", LiveStatus.Describe(client, now));
        Assert.Equal(ServerHealthState.Updating, ServerHealth.Evaluate(client, false, now));
    }

    [Fact]
    public void EveryRebootWait_HasAText()
    {
        Assert.All(Enum.GetValues<RebootWait>(), wait => Assert.NotEqual(wait.ToString(), LiveStatus.RebootWaitText(wait)));
        Assert.Equal(Enum.GetValues<AgentPhase>().Length, Enum.GetValues<AgentPhase>().Select(LiveStatus.PhaseLabel).Distinct().Count());
    }

    private static Task<HttpResponseMessage> SendActivityAsync(RolloutScenario scenario, RegisterResponse client, AgentActivity? activity) =>
        PostAsync(scenario.Http, client, new AgentActivityReport { Activity = activity });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, RegisterResponse client, AgentActivityReport report)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentActivity, UriKind.Relative)) { Content = JsonContent.Create(report) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task HeartbeatAsync(RolloutScenario scenario, RegisterResponse client, HeartbeatRequest heartbeat)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative)) { Content = JsonContent.Create(heartbeat) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<RebootWait?> RebootWaitAsync(RolloutScenario scenario, RegisterResponse client) =>
        scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == client.ClientId).Select(c => c.RebootWait).SingleAsync(TestContext.Current.CancellationToken));
}
