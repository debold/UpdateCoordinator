using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class ServerMoveTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task AnnouncedMove_ReachesApprovedAgents_AndTheirErrorsAreKeptUntilWithdrawn()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        Assert.Null((await HeartbeatAsync(scenario, client)).MoveTo);

        Assert.True((await scenario.WithServiceAsync<ServerMove, Outcome>(m => m.SetAsync(" https://New.example.local:8443 ", "admin", CancellationToken.None))).Succeeded);

        Assert.Equal(new Uri("https://new.example.local:8443/"), (await HeartbeatAsync(scenario, client, "The central server at https://new.example.local:8443/ has another trust anchor.")).MoveTo);
        Assert.Equal("The central server at https://new.example.local:8443/ has another trust anchor.", await ErrorAsync(scenario, client));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "ServerMove.Announced", TestContext.Current.CancellationToken)));

        Assert.True((await scenario.WithServiceAsync<ServerMove, Outcome>(m => m.SetAsync(null, "admin", CancellationToken.None))).Succeeded);
        Assert.Null(await ErrorAsync(scenario, client));

        // An error reported while no move is announced is not kept.
        Assert.Null((await HeartbeatAsync(scenario, client, "stale")).MoveTo);
        Assert.Null(await ErrorAsync(scenario, client));
    }

    [Fact]
    public async Task UnapprovedAgents_GetNoMove()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pending = await RegisterTests.RegisterAsync(scenario.Http);
        Assert.True((await scenario.WithServiceAsync<ServerMove, Outcome>(m => m.SetAsync("https://new.example.local/", "admin", CancellationToken.None))).Succeeded);

        var response = await HeartbeatAsync(scenario, pending, "error");

        Assert.Null(response.MoveTo);
        Assert.Null(await ErrorAsync(scenario, pending));
    }

    [Theory]
    [InlineData("http://new.example.local/")]
    [InlineData("new.example.local")]
    [InlineData("https://admin:pw@new.example.local/")]
    [InlineData("https://new.example.local/?x=1")]
    [InlineData("javascript:alert(1)")]
    public async Task InvalidAddress_IsRefused(string address)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var outcome = await scenario.WithServiceAsync<ServerMove, Outcome>(m => m.SetAsync(address, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == ServerMove.SettingKey, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task BackupPage_AnnouncesAndWithdraws_AndKeepsInputAfterAnError()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using (var refused = await browser.PostFormAsync("/administration/backup", new Dictionary<string, string> { ["_handler"] = "server-move", ["MoveAction"] = "save", ["MoveAddress"] = "http://new.example.local/" }))
        {
            var html = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Enter the address of the new central server", html, StringComparison.Ordinal);
            Assert.Contains("value=\"http://new.example.local/\"", html, StringComparison.Ordinal);
        }

        using (var announced = await browser.PostFormAndFollowAsync("/administration/backup", new Dictionary<string, string> { ["_handler"] = "server-move", ["MoveAction"] = "save", ["MoveAddress"] = "https://new.example.local/" }))
        {
            var html = await announced.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("The move was announced", html, StringComparison.Ordinal);
            Assert.Contains("value=\"https://new.example.local/\"", html, StringComparison.Ordinal);
        }

        using (await browser.PostFormAndFollowAsync("/administration/backup", new Dictionary<string, string> { ["_handler"] = "server-move", ["MoveAction"] = "withdraw", ["MoveAddress"] = "https://new.example.local/" }))
        {
        }

        Assert.False(await factory.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == ServerMove.SettingKey, TestContext.Current.CancellationToken)));
    }

    private static Task<string?> ErrorAsync(RolloutScenario scenario, RegisterResponse client) =>
        scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == client.ClientId).Select(c => c.ServerMoveError).SingleAsync(TestContext.Current.CancellationToken));

    private static async Task<HeartbeatResponse> HeartbeatAsync(RolloutScenario scenario, RegisterResponse client, string? moveError = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.2.3", ServerMoveError = moveError }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!;
    }
}
