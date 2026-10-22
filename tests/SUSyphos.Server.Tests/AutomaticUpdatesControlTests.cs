using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

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
            Assert.Contains("update source settings were saved", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal("true", await factory.WithDbAsync(db => db.Settings.Where(s => s.Key == SettingEntity.SystemManageAutomaticUpdates).Select(s => s.Value).SingleAsync(TestContext.Current.CancellationToken)));

        using (var invalid = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "automatic-updates", ["AutomaticUpdates"] = "<script>" }))
        {
            Assert.Contains("setting is invalid", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var microsoft = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "automatic-updates", ["AutomaticUpdates"] = "leave", ["MicrosoftUpdate"] = "microsoft" }))
        {
            Assert.Contains("update source settings were saved", await microsoft.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal("true", await factory.WithDbAsync(db => db.Settings.Where(s => s.Key == SettingEntity.SystemMicrosoftUpdate).Select(s => s.Value).SingleAsync(TestContext.Current.CancellationToken)));

        using (var invalid = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "automatic-updates", ["AutomaticUpdates"] = "leave", ["MicrosoftUpdate"] = "bing" }))
        {
            Assert.Contains("Microsoft Update setting is invalid", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MicrosoftUpdate_DefaultIsOff_GroupsOverride_AndWindowsUpdateOnlyWins()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        Assert.False((await scenario.ReleasesAsync(client)).UseMicrosoftUpdate);

        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSystemMicrosoftUpdateAsync(true, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await scenario.ReleasesAsync(client)).UseMicrosoftUpdate);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "MicrosoftUpdate.SystemDefaultChanged", TestContext.Current.CancellationToken)));

        await scenario.Factory.WithDbAsync(async db =>
        {
            db.Groups.Add(new GroupEntity { Id = 30, Name = "Windows only" });
            db.ClientGroups.Add(new ClientGroupEntity { ClientId = client.ClientId, GroupId = 30, Manual = true });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupMicrosoftUpdateAsync(30, false, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.ReleasesAsync(client)).UseMicrosoftUpdate);

        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupMicrosoftUpdateAsync(999, true, "admin", CancellationToken.None))).Succeeded);

        // A setting on the server wins over its groups; back to inherited, the group decides again.
        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientUpdateSourceAsync(client.ClientId, true, true, "admin", CancellationToken.None))).Succeeded);
        var released = await scenario.ReleasesAsync(client);
        Assert.True(released.UseMicrosoftUpdate);
        Assert.True(released.ManageAutomaticUpdates);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Client.UpdateSourceChanged", TestContext.Current.CancellationToken)));

        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientUpdateSourceAsync(client.ClientId, null, null, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.ReleasesAsync(client)).UseMicrosoftUpdate);
        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientUpdateSourceAsync(Guid.NewGuid(), true, true, "admin", CancellationToken.None))).Succeeded);
    }

    [Theory]
    [InlineData(false, new bool[0], false)]
    [InlineData(true, new bool[0], true)]
    [InlineData(false, new[] { true }, true)]
    [InlineData(true, new[] { true, false }, false)]
    public void Effective_GroupsDecide_AndTheCautiousChoiceWins(bool system, bool[] groups, bool expected)
    {
        Assert.Equal(expected, AssignmentService.Effective(system, [.. groups.Select(g => (bool?)g), null]));
    }

    [Fact]
    public void Text_NamesWhoSetTheValue()
    {
        Assert.Contains("set by SUSyphos", AutomaticUpdatesText.Describe(Contracts.Agents.AutomaticUpdateMode.AutoDownloadNotifyInstall, true, managedByAgent: true), StringComparison.Ordinal);
        Assert.Contains("group policy", AutomaticUpdatesText.Describe(Contracts.Agents.AutomaticUpdateMode.ScheduledInstall, true), StringComparison.Ordinal);
        Assert.Contains("0.3", AutomaticUpdatesText.Describe(null, false), StringComparison.Ordinal);
    }
}
