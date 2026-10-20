using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class ClientActivityTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task AgentEvents_AreListed_WithoutHeartbeats()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var boot = DateTimeOffset.UtcNow.AddDays(-3);

        await HeartbeatAsync(scenario, client, new HeartbeatRequest { AgentVersion = "1.2.3-alpha.0.4+abc123", LastBootAt = boot });
        await HeartbeatAsync(scenario, client, new HeartbeatRequest { AgentVersion = "1.2.3-alpha.0.4+abc123", LastBootAt = boot });
        using (var inventory = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory()))
        {
            Assert.Equal(HttpStatusCode.NoContent, inventory.StatusCode);
        }

        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        await scenario.InstallAsync(client, update.UpdateId, InstallResultCode.Failed);

        // A later boot and a new agent version are activities as well.
        await HeartbeatAsync(scenario, client, new HeartbeatRequest { AgentVersion = "1.3.0", LastBootAt = DateTimeOffset.UtcNow.AddMinutes(-5) });

        var kinds = await scenario.Factory.WithDbAsync(db => db.ClientActivities.Where(a => a.ClientId == client.ClientId).OrderBy(a => a.Id).Select(a => a.Kind).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [ClientActivityKind.Registered, ClientActivityKind.Inventory, ClientActivityKind.Search, ClientActivityKind.Installation, ClientActivityKind.Restarted, ClientActivityKind.AgentUpdated],
            kinds);

        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        using var page = await reader.GetAsync($"/servers/{client.ClientId}");
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Recent activity", html, StringComparison.Ordinal);
        Assert.Contains("Update search succeeded: 1 updates available.", html, StringComparison.Ordinal);
        Assert.Contains("Installation finished: 0 succeeded, 1 not successful.", html, StringComparison.Ordinal);
        Assert.Contains("to 1.3.0.", html, StringComparison.Ordinal);
        Assert.Contains("0 h 5 min (since", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Last update search</dt><dd><span class=\"pill ok\">OK</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_IsListedWithActorAndReason()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var client = await RegisterTests.RegisterAsync(http);
        factory.StartServer();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var approvals = scope.ServiceProvider.GetRequiredService<SUSyphos.Server.Approval.ClientApprovalService>();
            Assert.Equal(SUSyphos.Server.Approval.ApprovalResult.Done, await approvals.ApproveAsync(client.ClientId, "admin", "known server", CancellationToken.None));
        }

        var details = await factory.WithDbAsync(db => db.ClientActivities.Where(a => a.ClientId == client.ClientId).OrderBy(a => a.Id).Select(a => a.Detail).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Approved by admin: known server", details[^1]);
    }

    [Fact]
    public async Task OldEntries_AreDeleted()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var client = await RegisterTests.RegisterAsync(http);
        await factory.WithDbAsync(async db =>
        {
            db.ClientActivities.Add(new ClientActivityEntity { ClientId = client.ClientId, AtUtc = DateTime.UtcNow - ClientActivityLog.Retention - TimeSpan.FromDays(1), Kind = ClientActivityKind.Inventory, Detail = "old" });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<ClientActivityLog>().DeleteOldAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(["Registered"], await factory.WithDbAsync(db => db.ClientActivities.Where(a => a.ClientId == client.ClientId).Select(a => a.Kind.ToString()).ToListAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void EveryKind_HasItsOwnLabel()
    {
        Assert.All(Enum.GetValues<ClientActivityKind>(), kind => Assert.False(string.IsNullOrWhiteSpace(ClientActivityLog.Label(kind))));
        Assert.Equal(Enum.GetValues<ClientActivityKind>().Length, Enum.GetValues<ClientActivityKind>().Select(ClientActivityLog.Label).Distinct().Count());
    }

    private static async Task HeartbeatAsync(RolloutScenario scenario, RegisterResponse client, HeartbeatRequest heartbeat)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative)) { Content = JsonContent.Create(heartbeat) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
