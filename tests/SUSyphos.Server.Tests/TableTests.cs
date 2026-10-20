using System.Net;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Components;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class TableTests
{
    private const string Password = "correct horse battery";

    private sealed record Item(string Name, string Color, int Size);

    private static readonly IReadOnlyList<TableColumn<Item>> Columns =
    [
        new("name", "Name", i => i.Name),
        new("color", "Color", i => i.Color, i => [i.Color]),
        new("size", "Size", i => i.Size),
    ];

    private static readonly List<Item> Items = [.. Enumerable.Range(1, 60).Select(n => new Item($"item{n:00}", n % 3 == 0 ? "red" : "blue", n))];

    [Fact]
    public void Query_SortsFiltersSearchesAndPages()
    {
        var page = TablePaging.Apply(Items, TableQuery.Parse("/x?sort=size&dir=desc&f.color=red&page=2&q=ITEM", "name"), Columns, i => i.Name);

        Assert.Equal(20, page.Matching);
        Assert.Equal(60, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(["item09", "item06", "item03"], page.Rows.TakeLast(3).Select(i => i.Name));
        Assert.Equal([new FilterOption("blue", 40), new FilterOption("red", 20)], page.Options["color"]);
    }

    [Theory]
    [InlineData("/x?sort=nonsense&dir=up&page=-3")]
    [InlineData("/x?page=abc&f.color=%27%3B--&f.size=1")]
    [InlineData("/x?page=99999")]
    public void Query_ToleratesUnknownValues(string address)
    {
        var page = TablePaging.Apply(Items, TableQuery.Parse(address, "name"), Columns, i => i.Name);

        Assert.InRange(page.Page, 1, page.Pages);
        Assert.True(page.Rows.Count <= TableQuery.PageSize);
    }

    [Fact]
    public void Links_KeepTheRestOfTheView()
    {
        var query = TableQuery.Parse("/servers?q=sql&f.ring=Pilot&f.ring=Test&sort=name&page=3", "name");

        Assert.Equal("/servers?q=sql&f.ring=Pilot&f.ring=Test&sort=name&dir=desc", query.SortLink("name"));
        Assert.Equal("/servers?q=sql&f.ring=Test&sort=name", query.WithoutFilter("ring", "Pilot"));
        Assert.Contains("page=4", query.PageLink(4), StringComparison.Ordinal);
        Assert.DoesNotContain(query.HiddenFieldsExcept("q"), f => f.Key is "q" or "page");
        Assert.Equal(2, query.HiddenFieldsExcept("q").Count(f => f.Key == "f.ring"));
    }

    [Fact]
    public void Search_IsCappedAndLiteral()
    {
        var query = TableQuery.Parse("/x?q=" + new string('a', 500), "name");

        Assert.Equal(TableQuery.MaxSearchLength, query.Search!.Length);
        Assert.Empty(TablePaging.Apply(Items, TableQuery.Parse("/x?q=%25", "name"), Columns, i => i.Name).Rows);
    }

    [Theory]
    [InlineData("10.0.17763.6414", null, "Windows Server 2019", "17763.6414")]
    [InlineData("10.0.20348.2762", "Windows Server 2022 Datacenter", "Windows Server 2022", "20348.2762")]
    [InlineData("10.0.14393.7428", null, "Windows Server 2016", "14393.7428")]
    [InlineData("10.0.26100.2033", null, "Windows Server 2025", "26100.2033")]
    [InlineData("10.0.99999.1", "Windows Server vNext", "Windows Server vNext", "99999.1")]
    [InlineData("10.0.99999.1", null, "Windows (build 99999)", "99999.1")]
    [InlineData("garbage", null, "Unknown", "garbage")]
    public void OperatingSystem_IsReadable(string version, string? product, string name, string build)
    {
        Assert.Equal(name, OperatingSystems.Name(product, version));
        Assert.Equal(build, OperatingSystems.Build(version));
    }

    [Fact]
    public async Task ServerList_GroupsSortsAndQueuesBulkActionsForOperators()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        var ids = await SeedAsync(factory);

        using (var reader = new DashboardClient(factory))
        {
            using (await reader.LoginAsync("reader", Password))
            {
            }

            var grouped = await (await reader.GetAsync("/servers?group=os&sort=name&dir=desc")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Windows Server 2019 · 1", grouped, StringComparison.Ordinal);
            Assert.Contains("Windows Server 2022 · 2", grouped, StringComparison.Ordinal);
            Assert.True(grouped.IndexOf("WEB02", StringComparison.Ordinal) < grouped.IndexOf("WEB01", StringComparison.Ordinal));
            Assert.DoesNotContain("Search for updates now", grouped, StringComparison.Ordinal);
        }

        using var operatorBrowser = new DashboardClient(factory);
        using (await operatorBrowser.LoginAsync("operator", Password))
        {
        }

        using (var none = await operatorBrowser.PostFormAsync("/servers", new Dictionary<string, string> { ["_handler"] = "servers-bulk", ["Bulk"] = "search" }))
        {
            Assert.Contains("Select at least one server", await none.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var queued = await operatorBrowser.PostFormAsync("/servers", new Dictionary<string, string> { ["_handler"] = "servers-bulk", ["Bulk"] = "search", ["Selected"] = ids[0].ToString() }))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
            Assert.Contains("Queued for 1 servers", await queued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(1, await factory.WithDbAsync(db => db.AgentActions.CountAsync(a => a.ClientId == ids[0], TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task OverviewTiles_OpenTheServerListWithExactlyTheirFilter()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        await SeedAsync(factory);
        using var reader = new DashboardClient(factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var overview = await (await reader.GetAsync("/")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("href=\"/servers?f.status=Failed\"", overview, StringComparison.Ordinal);
        Assert.Contains("href=\"/servers?f.pending=Updates%20pending\"", overview, StringComparison.Ordinal);
        Assert.Contains("Servers reporting", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Installations running", overview, StringComparison.Ordinal);

        var reporting = await (await reader.GetAsync("/servers?f.status=OK&f.status=Warning&f.status=Reboot%20pending&f.status=Failed")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("SQL01", reporting, StringComparison.Ordinal);
        Assert.Contains("of 3 servers", reporting, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateList_ShowsRuleColumnsStatusAndFiltersByClassification()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var security = RolloutScenario.Update("Security Updates");
        var definition = RolloutScenario.Update("Definition Updates") with { UpdateId = Guid.NewGuid(), KbArticle = "2267602", Title = "Security Intelligence Update" };
        await scenario.ReportAsync(server, security, definition);
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var all = await (await reader.GetAsync("/updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var filtered = await (await reader.GetAsync("/updates?f.classification=Definition%20Updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Classification", all, StringComparison.Ordinal);
        Assert.Contains("Severity", all, StringComparison.Ordinal);
        Assert.Contains("class=\"donut small\"", all, StringComparison.Ordinal);
        Assert.Contains("KB2267602", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("KB5040000", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approvals_ApproveSelectedServersAtOnce()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        var ids = await SeedAsync(factory, RegistrationState.Pending);
        using var operatorBrowser = new DashboardClient(factory);
        using (await operatorBrowser.LoginAsync("operator", Password))
        {
        }

        var page = await (await operatorBrowser.GetAsync("/approvals")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Approve selected servers", page, StringComparison.Ordinal);
        Assert.Contains("class=\"active\"", page, StringComparison.Ordinal);

        using (var none = await operatorBrowser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "decision", ["Action"] = "approve-selected" }))
        {
            Assert.Contains("Select at least one server", await none.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var approved = await operatorBrowser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "decision", ["Action"] = "approve-selected", ["Selected"] = ids[1].ToString() }))
        {
            Assert.Contains("Approved 1 servers.", await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(1, await factory.WithDbAsync(db => db.Clients.CountAsync(c => c.State == RegistrationState.Approved, TestContext.Current.CancellationToken)));
    }

    private static Task<List<Guid>> SeedAsync(ServerFactory factory, RegistrationState state = RegistrationState.Approved)
    {
        factory.StartServer();
        return factory.WithDbAsync(async db =>
        {
            var ids = new List<Guid>();
            foreach (var (host, version) in new[] { ("SQL01", "10.0.17763.6414"), ("WEB01", "10.0.20348.2762"), ("WEB02", "10.0.20348.2762") })
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Clients.Add(new ClientEntity { Id = id, SecretHash = [1], HostName = host, OsVersion = version, AgentVersion = "0.2.0", State = state, RingId = RingEntity.ProductionId, LastHeartbeatAtUtc = DateTime.UtcNow });
            }

            await db.SaveChangesAsync();
            return ids;
        });
    }
}
