using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Debug mode of agents and central server, and acknowledged "Needs attention" topics (Marc, 2026-11-20/21).</summary>
public sealed class DebugModeTests
{
    private const string Password = "correct horse battery";

    private static async Task<HeartbeatResponse> HeartbeatAsync(RolloutScenario scenario, RegisterResponse client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0" }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<DashboardClient> LoginAsync(RolloutScenario scenario, string role, string userName = "user")
    {
        await scenario.Factory.CreateUserAsync(userName, Password, role);
        var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync(userName, Password))
        {
        }

        return browser;
    }

    private static async Task<string> HtmlAsync(DashboardClient browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ServerDebugMode_ReachesTheAgent_ShowsAsBannerAndAttention_AndEnds()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        Assert.Null((await HeartbeatAsync(scenario, client)).DebugUntil);

        using var admin = await LoginAsync(scenario, AdminRoles.Admin, "admin");
        using (await admin.PostFormAsync($"/servers/{client.ClientId}?dialog=debug", new Dictionary<string, string> { ["_handler"] = "debug", ["DebugHours"] = "4" }))
        {
        }

        var until = (await HeartbeatAsync(scenario, client)).DebugUntil;
        Assert.NotNull(until);
        Assert.InRange(until!.Value - DateTimeOffset.UtcNow, TimeSpan.FromHours(3.9), TimeSpan.FromHours(4));
        Assert.Contains("Debug mode", await HtmlAsync(admin, $"/servers/{client.ClientId}"), StringComparison.Ordinal);
        Assert.Contains("Debug mode is on for 1 server", await HtmlAsync(admin, "/"), StringComparison.Ordinal);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Debug.ServerOn" && a.Target == "SRV01", TestContext.Current.CancellationToken)));

        using (await admin.PostFormAsync($"/servers/{client.ClientId}", new Dictionary<string, string> { ["_handler"] = "debug-off" }))
        {
        }

        Assert.Null((await HeartbeatAsync(scenario, client)).DebugUntil);
        Assert.DoesNotContain("Debug mode is on", await HtmlAsync(admin, "/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredDebugMode_IsOff()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV02", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == client.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.DebugUntilUtc, DateTime.UtcNow.AddMinutes(-1)).SetProperty(c => c.DebugBy, "admin"), TestContext.Current.CancellationToken));

        Assert.Null((await HeartbeatAsync(scenario, client)).DebugUntil);
        Assert.Null(await scenario.WithServiceAsync<DebugMode, DebugState?>(d => d.ServerAsync(client.ClientId, CancellationToken.None)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(73)]
    [InlineData(-4)]
    public async Task DebugDurations_OutsideOneTo72Hours_AreRefused(int hours)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV03", RingEntity.PilotId);

        Assert.False((await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetServerAsync(client.ClientId, hours, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetCentralAsync(hours, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task OperatorsCannotSwitchTheDebugMode()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV04", RingEntity.PilotId);
        using var operatorBrowser = await LoginAsync(scenario, AdminRoles.Operator, "operator");

        using (await operatorBrowser.PostFormAsync($"/servers/{client.ClientId}?dialog=debug", new Dictionary<string, string> { ["_handler"] = "debug", ["DebugHours"] = "4" }))
        {
        }

        Assert.Null((await HeartbeatAsync(scenario, client)).DebugUntil);
    }

    [Fact]
    public async Task CentralDebugMode_ShowsABandOnEveryPage_AndEnds()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        using var admin = await LoginAsync(scenario, AdminRoles.Admin, "admin");

        Assert.True((await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetCentralAsync(1, "admin", CancellationToken.None))).Succeeded);
        Assert.Contains("class=\"debug-band\"", await HtmlAsync(admin, "/updates"), StringComparison.Ordinal);
        Assert.Contains("The central server is in debug mode", await HtmlAsync(admin, "/"), StringComparison.Ordinal);

        Assert.True((await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetCentralAsync(null, "admin", CancellationToken.None))).Succeeded);
        Assert.DoesNotContain("class=\"debug-band\"", await HtmlAsync(admin, "/updates"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcknowledgedTopic_HidesUntilMoreAreAffected_AndEndsWhenResolved()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var first = await scenario.AddClientAsync("SRV05", RingEntity.PilotId);
        await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetServerAsync(first.ClientId, 4, "admin", CancellationToken.None));
        using var operatorBrowser = await LoginAsync(scenario, AdminRoles.Operator, "operator");
        Assert.Contains("Debug mode is on for 1 server", await HtmlAsync(operatorBrowser, "/"), StringComparison.Ordinal);

        using (await operatorBrowser.PostFormAsync("/", new Dictionary<string, string> { ["_handler"] = "attention", ["AcknowledgeTopic"] = "debug-servers" }))
        {
        }

        var page = await HtmlAsync(operatorBrowser, "/");
        Assert.DoesNotContain("name=\"AcknowledgeTopic\" value=\"debug-servers\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"RestoreTopic\" value=\"debug-servers\"", page, StringComparison.Ordinal);
        Assert.Contains("1 acknowledged topic hidden", page, StringComparison.Ordinal);
        var stored = await scenario.Factory.WithDbAsync(db => db.AttentionAcknowledgements.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("debug-servers", 1, "operator"), (stored.Topic, stored.AffectedCount, stored.AcknowledgedBy));

        // More affected: it shows again.
        var second = await scenario.AddClientAsync("SRV06", RingEntity.PilotId);
        await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetServerAsync(second.ClientId, 4, "admin", CancellationToken.None));
        Assert.Contains("Debug mode is on for 2 servers", await HtmlAsync(operatorBrowser, "/"), StringComparison.Ordinal);

        // Resolved: the acknowledgement ends.
        await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetServerAsync(first.ClientId, null, "admin", CancellationToken.None));
        await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetServerAsync(second.ClientId, null, "admin", CancellationToken.None));
        _ = await HtmlAsync(operatorBrowser, "/");
        Assert.False(await scenario.Factory.WithDbAsync(db => db.AttentionAcknowledgements.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("Debug-Servers")]
    [InlineData("x'; DROP TABLE Clients;--")]
    [InlineData("")]
    public async Task InvalidTopics_AreRefused(string topic)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        Assert.False((await scenario.WithServiceAsync<AttentionAcknowledgements, Outcome>(a => a.AcknowledgeAsync(topic, 1, "title", "operator", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<AttentionAcknowledgements, Outcome>(a => a.RestoreAsync(topic, "operator", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task ReadersCannotAcknowledge()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV07", RingEntity.PilotId);
        await scenario.WithServiceAsync<DebugMode, Outcome>(d => d.SetServerAsync(client.ClientId, 4, "admin", CancellationToken.None));
        using var reader = await LoginAsync(scenario, AdminRoles.Reader, "reader");

        using (await reader.PostFormAsync("/", new Dictionary<string, string> { ["_handler"] = "attention", ["AcknowledgeTopic"] = "debug-servers" }))
        {
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.AttentionAcknowledgements.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task MyRequests_ListsOnlyTheUsersOwnActions_OpenAndJustFinished()
    {
        // Marc, 2026-11-20: "My requests" with a message when one finishes; only the user's own actions.
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV08", RingEntity.PilotId);
        using var mine = await LoginAsync(scenario, AdminRoles.Operator, "alice");
        using var other = await LoginAsync(scenario, AdminRoles.Operator, "bob");
        using (await mine.PostFormAsync($"/servers/{client.ClientId}", new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = "queue:SearchNow" }))
        {
        }

        using var response = await mine.GetAsync(MyRequests.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"server\":\"SRV08\"", json, StringComparison.Ordinal);
        Assert.Contains("\"open\":true", json, StringComparison.Ordinal);
        Assert.Contains("data-my-count", await HtmlAsync(mine, "/"), StringComparison.Ordinal);

        using var others = await other.GetAsync(MyRequests.Path);
        Assert.Equal("[]", await others.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Finished more than an hour ago: gone.
        await scenario.Factory.WithDbAsync(db => db.AgentActions.ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentActionState.Succeeded).SetProperty(a => a.CompletedAtUtc, DateTime.UtcNow.AddHours(-2)), TestContext.Current.CancellationToken));
        Assert.Empty(await scenario.WithServiceAsync<MyRequests, IReadOnlyList<MyRequest>>(m => m.ListAsync("alice", CancellationToken.None)));
    }

    [Fact]
    public async Task MyRequests_NeedsASignIn()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        using var anonymous = new DashboardClient(scenario.Factory);

        using var response = await anonymous.GetAsync(MyRequests.Path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
