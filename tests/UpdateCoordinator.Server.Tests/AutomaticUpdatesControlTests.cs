using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class AutomaticUpdatesControlTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Default_IsOff_AndTheSystemDefaultReachesTheReleaseList()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        Assert.False((await scenario.ReleasesAsync(client)).ManageAutomaticUpdates);

        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSystemManageAutomaticUpdatesAsync(true, "admin", CancellationToken.None))).Succeeded);

        Assert.True((await scenario.ReleasesAsync(client)).ManageAutomaticUpdates);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "AutomaticUpdates.SystemDefaultChanged", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Groups_OverrideTheDefault_AndLeaveUnchangedWins()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(async db =>
        {
            db.Groups.AddRange(new GroupEntity { Id = 20, Name = "Prevent" }, new GroupEntity { Id = 21, Name = "Leave" });
            db.ClientGroups.AddRange(new ClientGroupEntity { ClientId = client.ClientId, GroupId = 20, Manual = true }, new ClientGroupEntity { ClientId = client.ClientId, GroupId = 21, Manual = true });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupManageAutomaticUpdatesAsync(20, true, "admin", CancellationToken.None));

        Assert.True((await scenario.ReleasesAsync(client)).ManageAutomaticUpdates);

        await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupManageAutomaticUpdatesAsync(21, false, "admin", CancellationToken.None));
        Assert.False((await scenario.ReleasesAsync(client)).ManageAutomaticUpdates);

        // A system default "on" does not override a group that says "leave unchanged".
        await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSystemManageAutomaticUpdatesAsync(true, "admin", CancellationToken.None));
        Assert.False((await scenario.ReleasesAsync(client)).ManageAutomaticUpdates);

        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupManageAutomaticUpdatesAsync(999, true, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task MaintenancePage_SavesTheSystemDefault_AndRejectsUnknownValues()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using (var saved = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "automatic-updates", ["AutomaticUpdates"] = "prevent" }))
        {
            Assert.Contains("Automatic Updates setting was saved", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal("true", await factory.WithDbAsync(db => db.Settings.Where(s => s.Key == SettingEntity.SystemManageAutomaticUpdates).Select(s => s.Value).SingleAsync(TestContext.Current.CancellationToken)));

        using (var invalid = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "automatic-updates", ["AutomaticUpdates"] = "<script>" }))
        {
            Assert.Contains("setting is invalid", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Text_NamesWhoSetTheValue()
    {
        Assert.Contains("set by UpdateCoordinator", AutomaticUpdatesText.Describe(Contracts.Agents.AutomaticUpdateMode.AutoDownloadNotifyInstall, true, managedByAgent: true), StringComparison.Ordinal);
        Assert.Contains("group policy", AutomaticUpdatesText.Describe(Contracts.Agents.AutomaticUpdateMode.ScheduledInstall, true), StringComparison.Ordinal);
        Assert.Contains("0.3", AutomaticUpdatesText.Describe(null, false), StringComparison.Ordinal);
    }
}
