using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Approval;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class ClientLifecycleTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task SoftDelete_KeepsHistory_AndEndsTheCredentials()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        await scenario.InstallAsync(client, update.UpdateId, InstallResultCode.Succeeded);

        Assert.True((await scenario.WithServiceAsync<ClientLifecycle, Outcome>(l => l.SoftDeleteAsync(client.ClientId, "admin", CancellationToken.None))).Succeeded);

        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(scenario, client, "SRV01")).StatusCode);
        var stored = await scenario.Factory.WithDbAsync(db => db.Clients.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(RegistrationState.Deleted, stored.State);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.Installations.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Releases.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.False((await scenario.WithServiceAsync<ClientLifecycle, Outcome>(l => l.SoftDeleteAsync(client.ClientId, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task HardDelete_RemovesEverything()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        await scenario.InstallAsync(client, update.UpdateId, InstallResultCode.Succeeded);

        Assert.True((await scenario.WithServiceAsync<ClientLifecycle, Outcome>(l => l.HardDeleteAsync(client.ClientId, "admin", CancellationToken.None))).Succeeded);

        Assert.False(await scenario.Factory.WithDbAsync(db => db.Clients.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Installations.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Client.HardDeleted", TestContext.Current.CancellationToken)));
        Assert.False((await scenario.WithServiceAsync<ClientLifecycle, Outcome>(l => l.HardDeleteAsync(client.ClientId, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task Rename_IsNoCloneSuspicion()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        await HeartbeatAsync(scenario, client, "SRV01");
        await HeartbeatAsync(scenario, client, "SRV01-NEW");
        var last = await HeartbeatAsync(scenario, client, "SRV01-NEW");

        Assert.Equal(RegistrationState.Approved, (await last.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!.State);
    }

    [Fact]
    public async Task AlternatingHosts_BlockTheServer_UntilUnblocked()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        await HeartbeatAsync(scenario, client, "SRV01");
        await HeartbeatAsync(scenario, client, "SRV01-COPY");
        using var blocked = await HeartbeatAsync(scenario, client, "SRV01");

        Assert.Equal(RegistrationState.Blocked, (await blocked.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!.State);
        Assert.Equal(RegistrationState.Blocked, await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.State).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Client.CloneSuspected", TestContext.Current.CancellationToken)));

        using (var refused = await HeartbeatAsync(scenario, client, "SRV01"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using (var approvals = await browser.GetAsync("/approvals"))
        {
            Assert.Contains("SRV01-COPY", await approvals.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (await browser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "blocked", ["UnblockClient"] = client.ClientId.ToString() }))
        {
        }

        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(scenario, client, "SRV01")).StatusCode);
    }

    [Fact]
    public async Task UnblockingAServerThatIsNotBlocked_IsRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        Assert.False((await scenario.WithServiceAsync<ClientLifecycle, Outcome>(l => l.UnblockAsync(client.ClientId, "operator", CancellationToken.None))).Succeeded);
    }

    [Theory]
    [InlineData("bad host!")]
    [InlineData("<script>")]
    [InlineData("a-host-name-that-is-much-longer-than-the-sixty-three-characters-a-label-allows")]
    public async Task MalformedHostNameInHeartbeat_IsRejected(string host)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        using var response = await HeartbeatAsync(scenario, client, host);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RemovingNeedsAdministratorAndConfirmation()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        var path = $"/servers/{client.ClientId}?dialog=remove";

        using (var operatorBrowser = new DashboardClient(scenario.Factory))
        {
            using (await operatorBrowser.LoginAsync("operator", Password))
            {
            }

            using (await operatorBrowser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "remove", ["Remove"] = "hard", ["ConfirmRemove"] = "yes" }, pageToRead: "/"))
            {
            }
        }

        Assert.True(await scenario.Factory.WithDbAsync(db => db.Clients.AnyAsync(TestContext.Current.CancellationToken)));

        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        using (var unconfirmed = await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "remove", ["Remove"] = "soft", ["ConfirmRemove"] = string.Empty }))
        {
            Assert.Contains("Confirm the removal", await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "remove", ["Remove"] = "soft", ["ConfirmRemove"] = "yes" }))
        {
        }

        Assert.Equal(RegistrationState.Deleted, await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.State).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(AgentActionStatus.Succeeded, true)]
    [InlineData(AgentActionStatus.Rejected, false)]
    public async Task RemovalWithTheAgent_WaitsForTheAgent_AndRemovesOnlyWhenItUninstalled(AgentActionStatus status, bool removed)
    {
        // Marc, 2026-10-27: the agent uninstalls itself first, so it cannot register again.
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        Assert.True((await scenario.WithServiceAsync<ServerRemoval, Outcome>(r => r.RemoveAsync(client.ClientId, keepHistory: true, uninstallAgent: true, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<ServerRemoval, Outcome>(r => r.RemoveAsync(client.ClientId, keepHistory: true, uninstallAgent: true, "admin", CancellationToken.None))).Succeeded);
        Assert.Equal(RegistrationState.Approved, await StateAsync(scenario));

        var fetched = await scenario.WithServiceAsync<SUSyphos.Server.Agents.AgentActionService, AgentActionList>(a => a.FetchAsync(client.ClientId, CancellationToken.None));
        var action = Assert.Single(fetched.Actions);
        Assert.Equal(AgentActionKind.UninstallAgent, action.Kind);
        Assert.Equal(0, await scenario.WithServiceAsync<ServerRemoval, int>(r => r.CompleteAsync(CancellationToken.None)));
        Assert.True(await scenario.WithServiceAsync<SUSyphos.Server.Agents.AgentActionService, bool>(a => a.CompleteAsync(client.ClientId, action.Id, new AgentActionResult { Status = status, Detail = "done" }, CancellationToken.None)));

        await scenario.WithServiceAsync<ServerRemoval, int>(r => r.CompleteAsync(CancellationToken.None));

        Assert.Equal(removed ? RegistrationState.Deleted : RegistrationState.Approved, await StateAsync(scenario));
        Assert.Null(await scenario.WithServiceAsync<ServerRemoval, PendingRemoval?>(r => r.PendingAsync(client.ClientId, CancellationToken.None)));
        Assert.Equal(removed, await scenario.WithServiceAsync<ServerRemoval, bool>(r => r.WasRemovedAsync("srv01", null, CancellationToken.None)));
    }

    [Fact]
    public async Task UninstallingTheAgent_IsOnlyPartOfARemoval()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        Assert.False((await scenario.WithServiceAsync<SUSyphos.Server.Agents.AgentActionService, Outcome>(a => a.QueueAsync(client.ClientId, AgentActionKind.UninstallAgent, "admin", CancellationToken.None))).Succeeded);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RemovalPage_OffersOneDialog_AndShowsTheWaitingRemoval()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var path = $"/servers/{client.ClientId}?dialog=remove";
        var dialog = System.Net.WebUtility.HtmlDecode(await (await admin.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("name=\"UninstallAgent\"", dialog, StringComparison.Ordinal);
        Assert.Contains("Delete with the history", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("dialog=purge", dialog, StringComparison.Ordinal);

        using (var waiting = await admin.PostFormAndFollowAsync(path, new Dictionary<string, string> { ["_handler"] = "remove", ["Remove"] = "hard", ["ConfirmRemove"] = "yes", ["UninstallAgent"] = "yes" }))
        {
            Assert.Contains("Removal requested", await waiting.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(RegistrationState.Approved, await StateAsync(scenario));
    }

    private static Task<RegistrationState> StateAsync(RolloutScenario scenario) =>
        scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.State).SingleAsync(TestContext.Current.CancellationToken));


    private static async Task<HttpResponseMessage> HeartbeatAsync(RolloutScenario scenario, RegisterResponse client, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0", HostName = host }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        return await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
