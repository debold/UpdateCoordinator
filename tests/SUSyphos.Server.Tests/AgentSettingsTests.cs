using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Tests;

public sealed class AgentSettingsTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public void Combine_ServerWins_ThenTheLowestGroupNumber_ThenTheSystem()
    {
        var system = new Dictionary<string, string> { ["heartbeat-interval"] = "60", ["log-level"] = "Warning", ["search-interval"] = "8" };
        IReadOnlyList<IReadOnlyDictionary<string, string>> groups =
        [
            new Dictionary<string, string> { ["heartbeat-interval"] = "30", ["log-level"] = "Debug" },
            new Dictionary<string, string> { ["heartbeat-interval"] = "15", ["log-level"] = "Information" },
        ];
        var server = new Dictionary<string, string> { ["search-interval"] = "2" };

        var combined = AgentSettingValues.Combine(system, groups, server);

        Assert.Equal("15", combined["heartbeat-interval"]);
        Assert.Equal("Debug", combined["log-level"]);
        Assert.Equal("2", combined["search-interval"]);
        Assert.False(combined.ContainsKey("memory-limit"));
    }

    [Fact]
    public void Explain_NamesWhereEachValueComesFrom()
    {
        var explained = AgentSettingValues.Explain(
            new Dictionary<string, string> { ["log-level"] = "Warning" },
            [("Databases", new Dictionary<string, string> { ["heartbeat-interval"] = "30" })],
            new Dictionary<string, string> { ["memory-limit"] = "300" },
            "this server");

        Assert.Equal(("30", "group Databases"), explained.Where(e => e.Definition.Key == "heartbeat-interval").Select(e => (e.Value, e.Source)).Single());
        Assert.Equal(("Warning", "system default"), explained.Where(e => e.Definition.Key == "log-level").Select(e => (e.Value, e.Source)).Single());
        Assert.Equal(("300", "this server"), explained.Where(e => e.Definition.Key == "memory-limit").Select(e => (e.Value, e.Source)).Single());
        Assert.Equal(("4", "agent default"), explained.Where(e => e.Definition.Key == "search-interval").Select(e => (e.Value, e.Source)).Single());
    }

    [Fact]
    public async Task Settings_ReachTheAgentWithTheReleaseList()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        Assert.Null((await scenario.ReleasesAsync(client)).AgentSettings);

        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSystemAgentSettingsAsync(new Dictionary<string, string?> { ["heartbeat-interval"] = "60", ["include-drivers"] = "off" }, "admin", CancellationToken.None))).Succeeded);
        var groupId = await scenario.Factory.WithDbAsync(async db =>
        {
            var group = db.Groups.Add(new GroupEntity { Name = "Fast" }).Entity;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientGroups.Add(new ClientGroupEntity { ClientId = client.ClientId, GroupId = group.Id, Manual = true });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return group.Id;
        });
        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupAgentSettingsAsync(groupId, new Dictionary<string, string?> { ["heartbeat-interval"] = "20" }, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientAgentSettingsAsync(client.ClientId, new Dictionary<string, string?> { ["log-level"] = "Debug" }, "admin", CancellationToken.None))).Succeeded);

        var settings = (await scenario.ReleasesAsync(client)).AgentSettings!;
        Assert.Equal("20", settings["heartbeat-interval"]);
        Assert.Equal("false", settings["include-drivers"]);
        Assert.Equal("Debug", settings["log-level"]);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "AgentSettings.ClientChanged", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("heartbeat-interval", "1")]
    [InlineData("heartbeat-interval", "ten")]
    [InlineData("log-level", "Verbose")]
    [InlineData("restart-message", "Say \"hi\"")]
    [InlineData("CentralServer:BaseAddress", "https://evil.example/")]
    public async Task InvalidSettings_AreRefused(string key, string value)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var outcome = await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSystemAgentSettingsAsync(new Dictionary<string, string?> { [key] = value }, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == SettingEntity.SystemAgentSettings, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Page_SavesTheSystemDefaults_AndKeepsInputAfterAnError()
    {
        await using var factory = new SUSyphos.TestSupport.ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new SUSyphos.TestSupport.DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using (var refused = await browser.PostFormAsync("/administration/agent-settings", new Dictionary<string, string> { ["_handler"] = "agent-settings", ["AgentSetting.heartbeat-interval"] = "2", ["AgentSetting.search-interval"] = "6" }))
        {
            var html = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Heartbeat: a whole number from 5 to 600 seconds.", html, StringComparison.Ordinal);
            Assert.Contains("value=\"6\"", html, StringComparison.Ordinal);
        }

        using (await browser.PostFormAndFollowAsync("/administration/agent-settings", new Dictionary<string, string> { ["_handler"] = "agent-settings", ["AgentSetting.heartbeat-interval"] = "30", ["AgentSetting.search-interval"] = string.Empty }))
        {
        }

        var stored = await factory.WithDbAsync(db => db.Settings.Where(s => s.Key == SettingEntity.SystemAgentSettings).Select(s => s.Value).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"heartbeat-interval\":\"30\"}", stored);
    }
}
