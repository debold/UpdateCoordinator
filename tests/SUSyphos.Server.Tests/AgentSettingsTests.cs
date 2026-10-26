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

    [Fact]
    public async Task OneSetting_IsSetAndResetAlone_AndSavingTheGroupKeepsIt()
    {
        await using var factory = new SUSyphos.TestSupport.ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        var groupId = await factory.WithDbAsync(async db =>
        {
            var group = db.Groups.Add(new GroupEntity { Name = "Databases", AgentSettingsJson = "{\"log-level\":\"Debug\"}" }).Entity;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return group.Id;
        });
        using var browser = new SUSyphos.TestSupport.DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var dialog = $"/administration/groups?dialog=agent-setting&id={groupId}&key=heartbeat-interval";
        using (var refused = await browser.PostFormAsync(dialog, new Dictionary<string, string> { ["_handler"] = "agent-setting-edit", ["Value"] = "2" }))
        {
            Assert.Contains("Heartbeat: a whole number from 5 to 600 seconds.", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (await browser.PostFormAsync(dialog, new Dictionary<string, string> { ["_handler"] = "agent-setting-edit", ["Value"] = "30" }))
        {
        }

        Assert.Equal("{\"heartbeat-interval\":\"30\",\"log-level\":\"Debug\"}", await GroupSettingsAsync(factory, groupId));

        // Saving the group dialog leaves the agent settings alone.
        using (await browser.PostFormAsync($"/administration/groups?dialog=edit&id={groupId}", new Dictionary<string, string> { ["_handler"] = "group-dialog", ["Mode"] = "edit", ["Id"] = groupId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Name"] = "Databases", ["Percent"] = "25", ["Max"] = "5", ["WindowsInherit"] = "yes" }))
        {
        }

        Assert.Equal("{\"heartbeat-interval\":\"30\",\"log-level\":\"Debug\"}", await GroupSettingsAsync(factory, groupId));

        // "Inherit" removes it; the reset button removes the other.
        using (await browser.PostFormAsync(dialog, new Dictionary<string, string> { ["_handler"] = "agent-setting-edit", ["Inherit"] = "yes", ["Value"] = "30" }))
        {
        }

        using (await browser.PostFormAsync($"/administration/groups?dialog=edit&id={groupId}", new Dictionary<string, string> { ["_handler"] = "agent-setting-reset", ["ResetKey"] = "log-level" }))
        {
        }

        Assert.Null(await GroupSettingsAsync(factory, groupId));
    }

    [Fact]
    public async Task AppliedSettings_AreStored_AndAChangeGoesToTheActivityList()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var applied = new Dictionary<string, SUSyphos.Contracts.Agents.AppliedAgentSetting>
        {
            ["heartbeat-interval"] = new("30", SUSyphos.Contracts.Agents.AgentSettingSource.Central),
            ["log-level"] = new("Warning", SUSyphos.Contracts.Agents.AgentSettingSource.Local),
            ["CentralServer:BaseAddress"] = new("https://evil.example/", SUSyphos.Contracts.Agents.AgentSettingSource.Central),
            ["restart-message"] = new("bad\u0007text", SUSyphos.Contracts.Agents.AgentSettingSource.Central),
        };

        await HeartbeatAsync(scenario, client, applied);
        await HeartbeatAsync(scenario, client, applied);
        applied["heartbeat-interval"] = new("60", SUSyphos.Contracts.Agents.AgentSettingSource.Central);
        await HeartbeatAsync(scenario, client, applied);

        var stored = await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == client.ClientId).Select(c => c.AppliedAgentSettingsJson).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Contains("\"heartbeat-interval\":{\"Value\":\"60\"", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("restart-message", stored, StringComparison.Ordinal);
        var entries = await scenario.Factory.WithDbAsync(db => db.ClientActivities.Where(a => a.ClientId == client.ClientId && a.Kind == SUSyphos.Server.Agents.ClientActivityKind.AgentSettings).Select(a => a.Detail).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Contains("Heartbeat 60 seconds (central)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("60", SUSyphos.Contracts.Agents.AgentSettingSource.Central, null)]
    [InlineData("30", SUSyphos.Contracts.Agents.AgentSettingSource.Central, "not applied yet")]
    [InlineData("10", SUSyphos.Contracts.Agents.AgentSettingSource.AgentDefault, "not applied yet")]
    [InlineData("15", SUSyphos.Contracts.Agents.AgentSettingSource.Local, "server: 15 seconds")]
    public void AppliedValue_IsComparedWithTheCentralOne(string applied, SUSyphos.Contracts.Agents.AgentSettingSource source, string? expected)
    {
        var heartbeat = SUSyphos.Contracts.Agents.AgentSettings.Find("heartbeat-interval")!;

        var mark = AppliedAgentSettings.Compare(heartbeat, "60", new SUSyphos.Contracts.Agents.AppliedAgentSetting(applied, source));

        Assert.Equal(expected, mark?.Text);
        Assert.Null(AppliedAgentSettings.Compare(heartbeat, "60", null));
    }

    [Fact]
    public async Task ServerPage_MarksSettingsTheAgentAppliesDifferently_WithoutTheOldDialog()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await HeartbeatAsync(scenario, client, new Dictionary<string, SUSyphos.Contracts.Agents.AppliedAgentSetting>
        {
            ["log-level"] = new("Debug", SUSyphos.Contracts.Agents.AgentSettingSource.Local),
        });
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var configuration = await (await browser.GetAsync($"/servers/{client.ClientId}?tab=configuration")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var system = await (await browser.GetAsync($"/servers/{client.ClientId}?tab=system")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var dialog = await (await browser.GetAsync($"/servers/{client.ClientId}?dialog=agent-settings")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("server: Debug", configuration, StringComparison.Ordinal);
        Assert.Contains("1 applied differently", configuration, StringComparison.Ordinal);
        Assert.Contains("1 set differently on the server", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent settings on SRV01", dialog, StringComparison.Ordinal);
    }

    private static Task<string?> GroupSettingsAsync(SUSyphos.TestSupport.ServerFactory factory, int groupId) =>
        factory.WithDbAsync(db => db.Groups.Where(g => g.Id == groupId).Select(g => g.AgentSettingsJson).SingleAsync(TestContext.Current.CancellationToken));

    private static async Task HeartbeatAsync(RolloutScenario scenario, SUSyphos.Contracts.Agents.RegisterResponse client, IReadOnlyDictionary<string, SUSyphos.Contracts.Agents.AppliedAgentSetting> applied)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SUSyphos.Contracts.ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new SUSyphos.Contracts.Agents.HeartbeatRequest { AgentVersion = "1.2.3", AppliedSettings = applied }),
        };
        request.Headers.Authorization = SUSyphos.Contracts.Agents.AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
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
