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

        using var page = await reader.GetAsync($"/servers/{client.ClientId}?tab=activity");
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Recent activity", html, StringComparison.Ordinal);
        Assert.Contains("Update search succeeded: 1 updates available.", html, StringComparison.Ordinal);
        Assert.Contains("Installation finished: 0 succeeded, 1 not successful.", html, StringComparison.Ordinal);
        Assert.Contains("to 1.3.0.", html, StringComparison.Ordinal);

        using var system = await reader.GetAsync($"/servers/{client.ClientId}?tab=system");
        var facts = await system.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("0 h 5 min (since", facts, StringComparison.Ordinal);
        Assert.Contains("<dt>Last update search</dt><dd><span class=\"pill ok\">OK</span>", facts, StringComparison.Ordinal);
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
    public async Task ServerDetail_PagesEachListOnItsOwn()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var client = await RegisterTests.RegisterAsync(http);
        var start = DateTime.UtcNow.AddHours(-1);
        await factory.WithDbAsync(async db =>
        {
            for (var n = 1; n <= 30; n++)
            {
                db.ClientActivities.Add(new ClientActivityEntity { ClientId = client.ClientId, AtUtc = start.AddMinutes(n), Kind = n % 2 == 0 ? ClientActivityKind.Search : ClientActivityKind.Inventory, Detail = $"entry {n:00}" });
            }

            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        // Newest first (the registration, then entry 30), ten rows by default; the list states what it shows.
        var first = await PageAsync(reader, $"/servers/{client.ClientId}?tab=activity");
        Assert.Contains("entry 30", first, StringComparison.Ordinal);
        Assert.Contains("entry 22", first, StringComparison.Ordinal);
        Assert.DoesNotContain("entry 21", first, StringComparison.Ordinal);
        Assert.Contains("1–10 of 31 entries", first, StringComparison.Ordinal);
        Assert.Contains("name=\"act.size\"", first, StringComparison.Ordinal);

        // Page, size, search and filter of the activity list do not touch the other lists.
        var second = await PageAsync(reader, $"/servers/{client.ClientId}?tab=activity&act.page=2&hist.page=5");
        Assert.Contains("entry 21", second, StringComparison.Ordinal);
        Assert.DoesNotContain("entry 22", second, StringComparison.Ordinal);
        Assert.Contains("hist.page=5", second, StringComparison.Ordinal);

        var all = await PageAsync(reader, $"/servers/{client.ClientId}?tab=activity&act.size=50");
        Assert.Contains("entry 01", all, StringComparison.Ordinal);
        Assert.Contains("1–31 of 31 entries", all, StringComparison.Ordinal);

        var searched = await PageAsync(reader, $"/servers/{client.ClientId}?tab=activity&act.q=entry+0&act.f.activity={Uri.EscapeDataString(ClientActivityLog.Label(ClientActivityKind.Search))}");
        Assert.Contains("entry 08", searched, StringComparison.Ordinal);
        Assert.DoesNotContain("entry 07", searched, StringComparison.Ordinal);
        Assert.DoesNotContain("entry 12", searched, StringComparison.Ordinal);
        Assert.Contains("(filtered from 31)", searched, StringComparison.Ordinal);

        // Values outside the offered page sizes fall back to the default.
        var invalid = await PageAsync(reader, $"/servers/{client.ClientId}?tab=activity&act.size=100000&act.page=-1");
        Assert.Contains("1–10 of 31 entries", invalid, StringComparison.Ordinal);
    }

    private static async Task<string> PageAsync(DashboardClient browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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
