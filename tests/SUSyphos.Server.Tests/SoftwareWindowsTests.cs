using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Software windows of their own (Marc, 2026-11-13, variant A): system default, group and server, like maintenance windows.</summary>
public sealed class SoftwareWindowsTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task WithoutSoftwareWindows_TheAgentGetsNone_AndUsesTheMaintenanceWindows()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        Assert.Null((await scenario.ReleasesAsync(client)).SoftwareWindows);
    }

    [Fact]
    public async Task SystemGroupAndServer_SetSoftwareWindows_TheNearestWins()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        Task<Outcome> SetAsync(int? group, Guid? server, string? text) =>
            scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSoftwareWindowsAsync(group, server, text, "admin", CancellationToken.None));

        Assert.True((await SetAsync(null, null, "Mon-Fri 19:00-22:00")).Succeeded);
        Assert.Equal(5, (await scenario.ReleasesAsync(client)).SoftwareWindows!.Single().Days.Count);

        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.CreateGroupAsync("Web", null, null, "admin", CancellationToken.None))).Succeeded);
        var group = await scenario.Factory.WithDbAsync(db => db.Groups.Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientAssignmentAsync(client.ClientId, null, [group], null, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await SetAsync(group, null, "Sat 08:00-12:00")).Succeeded);
        Assert.Equal(DayOfWeek.Saturday, (await scenario.ReleasesAsync(client)).SoftwareWindows!.Single().Days.Single());

        Assert.True((await SetAsync(null, client.ClientId, "Sun 08:00-12:00")).Succeeded);
        Assert.Equal(DayOfWeek.Sunday, (await scenario.ReleasesAsync(client)).SoftwareWindows!.Single().Days.Single());

        // Back to inherited at every level: software follows the maintenance windows again.
        Assert.True((await SetAsync(null, client.ClientId, null)).Succeeded);
        Assert.True((await SetAsync(group, null, null)).Succeeded);
        Assert.True((await SetAsync(null, null, null)).Succeeded);
        Assert.Null((await scenario.ReleasesAsync(client)).SoftwareWindows);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "SoftwareWindows.Changed", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task InvalidSoftwareWindows_AreRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSoftwareWindowsAsync(null, null, "someday 25:00", "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSoftwareWindowsAsync(99, null, "Mon 19:00-22:00", "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task MaintenancePage_HasASoftwareTab_ThatSavesTheSoftwareWindows()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var page = await (await browser.GetAsync("/administration/maintenance?windows=software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Use the maintenance windows", page, StringComparison.Ordinal);
        Assert.Contains("Save software windows", page, StringComparison.Ordinal);

        using (await browser.PostFormAsync("/administration/maintenance?windows=software", new Dictionary<string, string>
        {
            ["_handler"] = "software-windows",
            ["SoftwareWindows.0.days"] = "3",
            ["SoftwareWindows.0.from"] = "19:00",
            ["SoftwareWindows.0.to"] = "22:00",
        }))
        {
        }

        Assert.NotNull(await scenario.WithServiceAsync<AssignmentService, IReadOnlyList<SUSyphos.Contracts.Agents.MaintenanceWindow>?>(s => s.GetSystemSoftwareWindowsAsync(CancellationToken.None)));
    }
}
