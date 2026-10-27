using System.Net;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Rings in the server list (the former ring pages, Marc 2026-10-25): the ring strip and moving servers to a ring.</summary>
public sealed class ServerListRingTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task RingStrip_ShowsTheRingsInOrder_AndTogglesThemInTheFilter()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        using var reader = await LoginAsync(scenario, AdminRoles.Reader);

        var all = await (await reader.GetAsync("/servers")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("class=\"ring-strip", all, StringComparison.Ordinal);
        Assert.True(all.IndexOf("1 Pilot <b>1</b>", StringComparison.Ordinal) < all.IndexOf("3 Production <b>1</b>", StringComparison.Ordinal), "Rings are listed in rollout order.");
        Assert.Contains("href=\"/servers?f.ring=Pilot\"", all, StringComparison.Ordinal);

        // With Pilot in the filter, its tag is on and removes it again; another ring is added to the filter.
        var pilot = await (await reader.GetAsync("/servers?f.ring=Pilot")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("PILOT01", pilot, StringComparison.Ordinal);
        Assert.DoesNotContain("PROD01", pilot, StringComparison.Ordinal);
        Assert.Contains("toggle on\" href=\"/servers\" aria-pressed=\"true\"", pilot, StringComparison.Ordinal);
        Assert.Contains("href=\"/servers?f.ring=Pilot&amp;f.ring=Production\"", pilot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administrator_MovesSelectedServersToARing()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        using var admin = await LoginAsync(scenario, AdminRoles.Admin);

        using (var moved = await admin.PostFormAsync("/servers", new Dictionary<string, string> { ["_handler"] = "servers-bulk", ["Bulk"] = $"ring:{RingEntity.PilotId}", ["Selected"] = test.ClientId.ToString() }))
        {
            Assert.Contains("Moved 1 servers to ring Pilot", await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var stored = await scenario.Factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == test.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(RingEntity.PilotId, stored.RingId);
        Assert.Equal(RingEntity.PilotId, stored.ManualRingId);
        Assert.Equal(RingEntity.ProductionId, await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == production.ClientId).Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Ring.ServersAdded", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(AdminRoles.Operator, "ring:1", "Only administrators")]
    [InlineData(AdminRoles.Admin, "ring:1", "Select at least one server")]
    [InlineData(AdminRoles.Admin, "ring:x", "Select at least one server")]
    public async Task Moving_NeedsAnAdministratorAndASelection(string role, string bulk, string message)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        using var browser = await LoginAsync(scenario, role);

        var fields = new Dictionary<string, string> { ["_handler"] = "servers-bulk", ["Bulk"] = bulk };
        if (role == AdminRoles.Operator)
        {
            fields["Selected"] = test.ClientId.ToString();
        }

        using (var refused = await browser.PostFormAsync("/servers", fields))
        {
            Assert.Contains(message, await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(RingEntity.TestId, await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.False((await scenario.WithServiceAsync<Assignment.AssignmentAdministration, Outcome>(a => a.MoveClientsToRingAsync([test.ClientId], 999, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task RingColumn_ShowsTheRingAndEachGroupAsTag_WithoutTheManualNote()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SQL01", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(async db =>
        {
            var cluster = new GroupEntity { Name = "SQL cluster" };
            var all = new GroupEntity { Name = "My servers" };
            db.Groups.AddRange(cluster, all);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientGroups.AddRange(new ClientGroupEntity { ClientId = server.ClientId, GroupId = cluster.Id, Manual = true }, new ClientGroupEntity { ClientId = server.ClientId, GroupId = all.Id });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        using var reader = await LoginAsync(scenario, AdminRoles.Reader);

        var html = await (await reader.GetAsync("/servers")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains(">Ring and groups<", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tag\">SQL cluster</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tag\">My servers</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"note\">manual</div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RingPages_AreGone()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        using var reader = await LoginAsync(scenario, AdminRoles.Reader);

        using var response = await reader.GetAsync($"/rings/{RingEntity.PilotId}");
        var overview = await (await reader.GetAsync("/")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("href=\"/rings", overview, StringComparison.Ordinal);
    }

    private static async Task<DashboardClient> LoginAsync(RolloutScenario scenario, string role)
    {
        await scenario.Factory.CreateUserAsync("user", Password, role);
        var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        return browser;
    }
}
