using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class RingAddServersTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Administrator_MovesSelectedServersIntoTheRing()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        using var admin = await LoginAsync(scenario, AdminRoles.Admin);

        var dialog = await (await admin.GetAsync($"/rings/{RingEntity.PilotId}?dialog=add")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("TEST01", dialog, StringComparison.Ordinal);
        Assert.Contains("PROD01", dialog, StringComparison.Ordinal);
        Assert.True(dialog.IndexOf("TEST01", StringComparison.Ordinal) < dialog.IndexOf("PROD01", StringComparison.Ordinal), "Servers are listed by ring order.");

        using (var moved = await admin.PostFormAsync($"/rings/{RingEntity.PilotId}?dialog=add", new Dictionary<string, string> { ["_handler"] = "ring-add", ["Move"] = test.ClientId.ToString() }))
        {
            Assert.Contains("1 server(s) moved to Pilot.", await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(RingEntity.PilotId, await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == test.ClientId).Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(RingEntity.ProductionId, await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == production.ClientId).Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Ring.ServersAdded", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Reader_CannotMoveServers_AndNothingSelectedIsRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        using var reader = await LoginAsync(scenario, AdminRoles.Reader);

        using (var refused = await reader.PostFormAsync($"/rings/{RingEntity.PilotId}?dialog=add", new Dictionary<string, string> { ["_handler"] = "ring-add", ["Move"] = test.ClientId.ToString() }))
        {
            Assert.Contains("Only administrators", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(RingEntity.TestId, await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.False((await scenario.WithServiceAsync<Assignment.AssignmentAdministration, Outcome>(a => a.MoveClientsToRingAsync([], RingEntity.PilotId, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<Assignment.AssignmentAdministration, Outcome>(a => a.MoveClientsToRingAsync([test.ClientId], 999, "admin", CancellationToken.None))).Succeeded);
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
