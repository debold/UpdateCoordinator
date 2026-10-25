using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class MaintenanceWindowFormatTests
{
    [Fact]
    public void WeekdayRangeOverMidnight_IsParsed()
    {
        Assert.True(MaintenanceWindowFormat.TryParse("Mon-Fri 19:00-05:00", out var windows, out _));

        var window = Assert.Single(windows);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], window.Days);
        Assert.Equal(TimeSpan.FromHours(19), window.Start);
        Assert.Equal(TimeSpan.FromHours(5), window.End);
    }

    [Theory]
    [InlineData("sat,SUN 00:00-24:00", 2)]
    [InlineData("Fri-Sun 22:00-06:00", 3)]
    [InlineData("Mon,Mon 01:00-02:00", 1)]
    public void Variants_AreParsed(string text, int days)
    {
        Assert.True(MaintenanceWindowFormat.TryParse(text, out var windows, out _));
        Assert.Equal(days, Assert.Single(windows).Days.Count);
    }

    [Fact]
    public void EmptyText_MeansNoWindows()
    {
        Assert.True(MaintenanceWindowFormat.TryParse("  \n ", out var windows, out _));
        Assert.Empty(windows);
    }

    [Theory]
    [InlineData("Monday 19:00-05:00")]
    [InlineData("Mon 25:00-05:00")]
    [InlineData("Mon 19:00")]
    [InlineData("Mon 19:00-00:00")]
    [InlineData("Mon 7:00-08:00")]
    [InlineData("19:00-05:00")]
    [InlineData("Mon-Xyz 19:00-05:00")]
    [InlineData("Mon 19:00-05:00 extra")]
    [InlineData("<script> 19:00-05:00")]
    public void InvalidLine_IsRejectedWithMessage(string text)
    {
        Assert.False(MaintenanceWindowFormat.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TooManyWindows_AreRejected()
    {
        var text = string.Join('\n', Enumerable.Repeat("Mon 01:00-02:00", MaintenanceWindow.MaxWindows + 1));

        Assert.False(MaintenanceWindowFormat.TryParse(text, out _, out _));
    }

    [Fact]
    public void FormatAndParse_RoundTrip()
    {
        var text = MaintenanceWindowFormat.Format(MaintenanceWindow.SystemDefault);

        Assert.Equal("Mon,Tue,Wed,Thu,Fri 19:00-05:00\nSat,Sun 00:00-24:00", text);
        Assert.True(MaintenanceWindowFormat.TryParse(text, out var parsed, out _));
        Assert.Equal(MaintenanceWindowFormat.ToJson(MaintenanceWindow.SystemDefault), MaintenanceWindowFormat.ToJson(parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[{\"days\":[9],\"start\":\"01:00:00\",\"end\":\"02:00:00\"}]")]
    public void InvalidStoredJson_FallsBackToNull(string? json)
    {
        Assert.Null(MaintenanceWindowFormat.FromJson(json));
    }
}

public sealed class AssignmentServiceTests
{
    [Fact]
    public async Task NewClient_LandsInProductionWithSystemWindows()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var credentials = await RegisterTests.RegisterAsync(client);

        var stored = await GetAsync(factory, credentials.ClientId);
        Assert.Equal(RingEntity.ProductionId, stored.RingId);
        Assert.Equal(["Pilot", "Test", "Production"], await factory.WithDbAsync(db => db.Rings.OrderBy(r => r.Order).Select(r => r.Name).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(MaintenanceWindowFormat.ToJson(MaintenanceWindow.SystemDefault), stored.EffectiveWindowsJson);
    }

    [Fact]
    public async Task RingRule_ByRole_AssignsRingAfterInventory()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        await WithAdministrationAsync(factory, admin => admin.CreateRuleAsync(10, AssignmentTarget.Ring, RingEntity.PilotId, "DomainController", null, null, "admin", CancellationToken.None));
        var credentials = await RegisterTests.RegisterAsync(client);

        using (await InventoryTests.SendAsync(client, credentials, InventoryTests.ValidInventory()))
        {
        }

        Assert.Equal(RingEntity.PilotId, (await GetAsync(factory, credentials.ClientId)).RingId);
    }

    [Fact]
    public async Task LowestPriorityRingRule_Wins_AndManualOverrideBeatsRules()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        await WithAdministrationAsync(factory, admin => admin.CreateRuleAsync(20, AssignmentTarget.Ring, RingEntity.ProductionId, null, null, "SRV*", "admin", CancellationToken.None));
        await WithAdministrationAsync(factory, admin => admin.CreateRuleAsync(10, AssignmentTarget.Ring, RingEntity.TestId, null, "example.local", null, "admin", CancellationToken.None));
        var credentials = await RegisterTests.RegisterAsync(client);
        Assert.Equal(RingEntity.TestId, (await GetAsync(factory, credentials.ClientId)).RingId);

        await WithAdministrationAsync(factory, admin => admin.SetClientAssignmentAsync(credentials.ClientId, RingEntity.PilotId, [], null, "admin", CancellationToken.None));

        Assert.Equal(RingEntity.PilotId, (await GetAsync(factory, credentials.ClientId)).RingId);
    }

    [Fact]
    public async Task GroupRule_AddsMembership_ManualMembershipSurvivesRuleRemoval()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        await WithAdministrationAsync(factory, admin => admin.CreateGroupAsync("Domain controllers", null, null, "admin", CancellationToken.None));
        await WithAdministrationAsync(factory, admin => admin.CreateGroupAsync("Manual", null, null, "admin", CancellationToken.None));
        var dcGroup = await factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "Domain controllers").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        var manualGroup = await factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "Manual").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        await WithAdministrationAsync(factory, admin => admin.CreateRuleAsync(1, AssignmentTarget.Group, dcGroup, null, null, "SRV01", "admin", CancellationToken.None));
        var credentials = await RegisterTests.RegisterAsync(client);
        await WithAdministrationAsync(factory, admin => admin.SetClientAssignmentAsync(credentials.ClientId, null, [manualGroup], null, "admin", CancellationToken.None));

        Assert.Equal([dcGroup, manualGroup], (await GroupsAsync(factory, credentials.ClientId)).Order());

        var ruleId = await factory.WithDbAsync(db => db.AssignmentRules.Select(r => r.Id).SingleAsync(TestContext.Current.CancellationToken));
        await WithAdministrationAsync(factory, admin => admin.DeleteRuleAsync(ruleId, "admin", CancellationToken.None));

        Assert.Equal([manualGroup], await GroupsAsync(factory, credentials.ClientId));
    }

    [Fact]
    public async Task Windows_AreInheritedSystemThenGroupThenServer_AndBumpTheReleaseVersion()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        var initialVersion = (await GetAsync(factory, credentials.ClientId)).ReleasesVersion;

        await WithAdministrationAsync(factory, admin => admin.SetSystemWindowsAsync("Sun 02:00-04:00", "admin", CancellationToken.None));
        var afterSystem = await GetAsync(factory, credentials.ClientId);
        Assert.Contains("\"start\":\"02:00:00\"", afterSystem.EffectiveWindowsJson, StringComparison.Ordinal);
        Assert.True(afterSystem.ReleasesVersion > initialVersion);

        await WithAdministrationAsync(factory, admin => admin.CreateGroupAsync("Night", null, "Wed 01:00-03:00", "admin", CancellationToken.None));
        var groupId = await factory.WithDbAsync(db => db.Groups.Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        await WithAdministrationAsync(factory, admin => admin.SetClientAssignmentAsync(credentials.ClientId, null, [groupId], null, "admin", CancellationToken.None));
        Assert.Contains("\"start\":\"01:00:00\"", (await GetAsync(factory, credentials.ClientId)).EffectiveWindowsJson, StringComparison.Ordinal);

        await WithAdministrationAsync(factory, admin => admin.SetClientAssignmentAsync(credentials.ClientId, null, [groupId], "Thu 05:00-06:00", "admin", CancellationToken.None));
        var afterServer = await GetAsync(factory, credentials.ClientId);
        Assert.Contains("\"start\":\"05:00:00\"", afterServer.EffectiveWindowsJson, StringComparison.Ordinal);

        // Recalculating without a change does not bump the version again.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AssignmentService>().RecalculateAsync(null, TestContext.Current.CancellationToken);
        }

        Assert.Equal(afterServer.ReleasesVersion, (await GetAsync(factory, credentials.ClientId)).ReleasesVersion);
    }

    [Fact]
    public async Task ApprovedAgent_ReceivesEffectiveWindowsWithReleases()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved), TestContext.Current.CancellationToken));
        await WithAdministrationAsync(factory, admin => admin.SetClientAssignmentAsync(credentials.ClientId, null, [], "Tue 03:00-04:00", "admin", CancellationToken.None));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentReleases, UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var releases = await response.Content.ReadFromJsonAsync<ReleaseList>(TestContext.Current.CancellationToken);

        var window = Assert.Single(releases!.MaintenanceWindows!);
        Assert.Equal([DayOfWeek.Tuesday], window.Days);
        Assert.Equal(TimeSpan.FromHours(3), window.Start);
    }

    [Theory]
    [InlineData("SRV*", "SRV01", true)]
    [InlineData("srv??", "SRV01", true)]
    [InlineData("SRV?", "SRV01", false)]
    [InlineData("SQL*", "SRV01", false)]
    [InlineData("S.V01", "SRV01", false)]
    public void WildcardPatterns_MatchLiterally(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, Wildcard.Matches(pattern, host));
    }

    internal static async Task WithAdministrationAsync(ServerFactory factory, Func<AssignmentAdministration, Task<Outcome>> action)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        var outcome = await action(scope.ServiceProvider.GetRequiredService<AssignmentAdministration>());
        Assert.True(outcome.Succeeded, outcome.Error);
    }

    private static Task<ClientEntity> GetAsync(ServerFactory factory, Guid clientId) =>
        factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == clientId, TestContext.Current.CancellationToken));

    internal static Task<List<int>> GroupsAsync(ServerFactory factory, Guid clientId) =>
        factory.WithDbAsync(db => db.ClientGroups.Where(m => m.ClientId == clientId).Select(m => m.GroupId).ToListAsync(TestContext.Current.CancellationToken));
}

public sealed class AssignmentAdministrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" leading space")]
    [InlineData("<script>")]
    [InlineData("a name that is far too long for a group because it has more than sixty-four characters")]
    public async Task InvalidGroupName_IsRefused(string name)
    {
        var outcome = await RunAsync(admin => admin.CreateGroupAsync(name, null, null, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task DuplicateGroupName_IsRefused()
    {
        await using var factory = new ServerFactory();
        Assert.True((await RunAsync(factory, admin => admin.CreateGroupAsync("Web", null, null, "admin", CancellationToken.None))).Succeeded);

        Assert.False((await RunAsync(factory, admin => admin.CreateGroupAsync("Web", null, null, "admin", CancellationToken.None))).Succeeded);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Not A Role", null, null)]
    [InlineData(null, "example..local", null)]
    [InlineData(null, null, "SRV(.*)")]
    [InlineData(null, null, "SRV 01")]
    public async Task InvalidRule_IsRefused(string? role, string? domain, string? pattern)
    {
        var outcome = await RunAsync(admin => admin.CreateRuleAsync(1, AssignmentTarget.Ring, RingEntity.PilotId, role, domain, pattern, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Theory]
    [InlineData(-1, (int)AssignmentTarget.Ring, RingEntity.PilotId)]
    [InlineData(1, (int)AssignmentTarget.Ring, 99)]
    [InlineData(1, (int)AssignmentTarget.Group, 99)]
    [InlineData(1, 7, 1)]
    public async Task InvalidPriorityOrTarget_IsRefused(int priority, int target, int targetId)
    {
        var outcome = await RunAsync(admin => admin.CreateRuleAsync(priority, (AssignmentTarget)target, targetId, "DomainController", null, null, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task DuplicatePriority_IsRefused_OnCreateAndEdit()
    {
        await using var factory = new ServerFactory();
        Assert.True((await RunAsync(factory, admin => admin.CreateRuleAsync(100, AssignmentTarget.Ring, RingEntity.PilotId, "DomainController", null, null, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await RunAsync(factory, admin => admin.CreateRuleAsync(101, AssignmentTarget.Ring, RingEntity.TestId, null, "example.local", null, "admin", CancellationToken.None))).Succeeded);

        var duplicate = await RunAsync(factory, admin => admin.CreateRuleAsync(100, AssignmentTarget.Ring, RingEntity.TestId, null, null, "SRV*", "admin", CancellationToken.None));
        var second = await factory.WithDbAsync(db => db.AssignmentRules.Where(r => r.Priority == 101).Select(r => r.Id).SingleAsync(TestContext.Current.CancellationToken));
        var edited = await RunAsync(factory, admin => admin.UpdateRuleAsync(second, 100, AssignmentTarget.Ring, RingEntity.TestId, null, "example.local", null, "admin", CancellationToken.None));
        var keepOwn = await RunAsync(factory, admin => admin.UpdateRuleAsync(second, 101, AssignmentTarget.Ring, RingEntity.TestId, null, "example.local", null, "admin", CancellationToken.None));

        Assert.False(duplicate.Succeeded);
        Assert.Contains("e.g. 102", duplicate.Error, StringComparison.Ordinal);
        Assert.False(edited.Succeeded);
        Assert.True(keepOwn.Succeeded);
    }

    [Fact]
    public async Task DuplicatePriorities_FromEarlierVersions_AreRenumberedInOrder()
    {
        await using var factory = new ServerFactory();
        await factory.WithDbAsync(async db =>
        {
            db.AssignmentRules.AddRange(
                new AssignmentRuleEntity { Priority = 10, Target = AssignmentTarget.Ring, TargetId = RingEntity.PilotId, Domain = "a.local" },
                new AssignmentRuleEntity { Priority = 10, Target = AssignmentTarget.Ring, TargetId = RingEntity.TestId, Domain = "b.local" },
                new AssignmentRuleEntity { Priority = 11, Target = AssignmentTarget.Ring, TargetId = RingEntity.TestId, Domain = "c.local" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AssignmentAdministration>().RenumberDuplicatePrioritiesAsync(CancellationToken.None));
        }

        var priorities = await factory.WithDbAsync(db => db.AssignmentRules.OrderBy(r => r.Domain).Select(r => r.Priority).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([10, 12, 11], priorities);
    }

    [Fact]
    public async Task ServersAreAddedToAGroup_InBulk_KeepingTheirOtherGroups()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        await AssignmentServiceTests.WithAdministrationAsync(factory, admin => admin.CreateGroupAsync("Web", null, null, "admin", CancellationToken.None));
        await AssignmentServiceTests.WithAdministrationAsync(factory, admin => admin.CreateGroupAsync("Other", null, null, "admin", CancellationToken.None));
        var web = await factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "Web").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        var other = await factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "Other").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        var first = await RegisterTests.RegisterAsync(client);
        var second = await RegisterTests.RegisterAsync(client);
        await AssignmentServiceTests.WithAdministrationAsync(factory, admin => admin.SetClientAssignmentAsync(first.ClientId, null, [other], null, "admin", CancellationToken.None));

        await using var scope = factory.Services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<AssignmentAdministration>();
        var (outcome, added) = await administration.AddClientsToGroupAsync([first.ClientId, second.ClientId, Guid.NewGuid()], web, "admin", CancellationToken.None);
        var (again, addedAgain) = await administration.AddClientsToGroupAsync([first.ClientId], web, "admin", CancellationToken.None);
        var (missingGroup, _) = await administration.AddClientsToGroupAsync([first.ClientId], 999, "admin", CancellationToken.None);
        var (nobody, _) = await administration.AddClientsToGroupAsync([Guid.NewGuid()], web, "admin", CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, added);
        Assert.True(again.Succeeded);
        Assert.Equal(0, addedAgain);
        Assert.False(missingGroup.Succeeded);
        Assert.False(nobody.Succeeded);
        Assert.Equal(new[] { web, other }.Order(), (await AssignmentServiceTests.GroupsAsync(factory, first.ClientId)).Order());
        Assert.Equal([web], await AssignmentServiceTests.GroupsAsync(factory, second.ClientId));
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Group.ServersAdded", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void NextFreePriority_FollowsTheHighestRule_AndStaysBelowACeiling()
    {
        Assert.Equal(100, RulePriority.NextFree([]));
        Assert.Equal(121, RulePriority.NextFree([5, 120]));
        Assert.Equal(101, RulePriority.NextFree([100, 9000], below: 9000));
        Assert.Equal(102, RulePriority.NextFree([100, 101, 103], below: 103));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mon 99:00-01:00")]
    public async Task InvalidSystemWindows_AreRefused(string text)
    {
        Assert.False((await RunAsync(admin => admin.SetSystemWindowsAsync(text, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task UnknownClientOrRing_IsRefused()
    {
        await using var factory = new ServerFactory();
        using var http = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);

        Assert.False((await RunAsync(factory, admin => admin.SetClientAssignmentAsync(Guid.NewGuid(), null, [], null, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await RunAsync(factory, admin => admin.SetClientAssignmentAsync(credentials.ClientId, 99, [], null, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await RunAsync(factory, admin => admin.SetClientAssignmentAsync(credentials.ClientId, null, [99], null, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task Changes_AreAudited()
    {
        await using var factory = new ServerFactory();

        await RunAsync(factory, admin => admin.CreateGroupAsync("Audited", null, null, "alice", CancellationToken.None));

        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Actor == "alice" && e.Action == "Group.Created", TestContext.Current.CancellationToken)));
    }

    private static async Task<Outcome> RunAsync(Func<AssignmentAdministration, Task<Outcome>> action)
    {
        await using var factory = new ServerFactory();
        return await RunAsync(factory, action);
    }

    private static async Task<Outcome> RunAsync(ServerFactory factory, Func<AssignmentAdministration, Task<Outcome>> action)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AssignmentAdministration>());
    }
}

public sealed class AssignmentPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task RuleList_CountsAndLinksTheServersARuleMatches()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var matched = await scenario.AddClientAsync("SQL01", RingEntity.PilotId);
        await scenario.AddClientAsync("WEB01", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(async db =>
        {
            var groupId = db.Groups.Add(new GroupEntity { Name = "Databases" }).Entity;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.AssignmentRules.Add(new AssignmentRuleEntity { Priority = 10, Target = AssignmentTarget.Group, TargetId = groupId.Id, HostNamePattern = "SQL*" });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        var ruleId = await scenario.Factory.WithDbAsync(db => db.AssignmentRules.Select(r => r.Id).SingleAsync(TestContext.Current.CancellationToken));
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var rules = await (await browser.GetAsync("/administration/rules")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"<a href=\"/servers?rule={ruleId}\"", rules, StringComparison.Ordinal);
        Assert.Contains(">1</a>", rules, StringComparison.Ordinal);
        Assert.Contains("href=\"/servers?f.group=Databases\"", rules, StringComparison.Ordinal);

        var servers = await (await browser.GetAsync($"/servers?rule={ruleId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"/servers/{matched.ClientId}", servers, StringComparison.Ordinal);
        Assert.DoesNotContain(">WEB01<", servers, StringComparison.Ordinal);
        Assert.Contains("Assignment rule 10", servers, StringComparison.Ordinal);

        // An unknown rule is ignored.
        Assert.Contains(">WEB01<", await (await browser.GetAsync("/servers?rule=999")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemBox_ShowsTheReportedUpdateSource_AndADifferentSetting()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, SUSyphos.Contracts.Agents.RegistrationState.Approved), TestContext.Current.CancellationToken));
        var inventory = InventoryTests.ValidInventory();
        using (await InventoryTests.SendAsync(browser.Http, credentials, inventory with { UpdateSource = inventory.UpdateSource with { MicrosoftUpdateRegistered = true } }))
        {
        }

        using (await browser.LoginAsync("reader", Password))
        {
        }

        // Registered by the agent, but the setting (system default: Windows Update only) says otherwise.
        var html = await (await browser.GetAsync($"/servers/{credentials.ClientId}?tab=system")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Microsoft Update (Windows and other Microsoft products)", html, StringComparison.Ordinal);
        Assert.Contains("set: Windows Update only", html, StringComparison.Ordinal);
        Assert.True(await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId).Select(c => c.MicrosoftUpdateRegistered == true).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Reader_SeesServersButCannotChangeAssignment()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        using var list = await browser.GetAsync("/servers");
        using var detail = await browser.GetAsync($"/servers/{credentials.ClientId}");
        using var post = await browser.PostFormAsync($"/servers/{credentials.ClientId}", new Dictionary<string, string> { ["_handler"] = "assignment", ["Ring"] = RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, pageToRead: "/");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("SRV01", await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain("formname=\"assignment\"", await detail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(RingEntity.ProductionId, await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId).Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Admin_ChangesAssignmentThroughForm()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var response = await browser.PostFormAndFollowAsync($"/servers/{credentials.ClientId}?tab=configuration", new Dictionary<string, string>
        {
            ["_handler"] = "assignment",
            ["Ring"] = RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Windows"] = "Sat 01:00-02:00",
        });

        Assert.Contains("The assignment was saved.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var stored = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(RingEntity.PilotId, stored.RingId);
        Assert.Equal(RingEntity.PilotId, stored.ManualRingId);
    }

    [Fact]
    public async Task Admin_GetsValidationMessageForInvalidWindows()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var response = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "maintenance", ["Windows.0.days"] = "1", ["Windows.0.from"] = "25:00", ["Windows.0.to"] = "01:00" });

        Assert.Contains("Invalid window", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleDialog_PreviewsWhichServersMove_WithoutSaving()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.AddClientAsync("SQL01", RingEntity.ProductionId);
        await scenario.AddClientAsync("WEB01", RingEntity.ProductionId);
        // The scenario sets every ring by hand; SQL01 is left to the rules, WEB01 keeps its manual ring.
        await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.HostName == "SQL01").ExecuteUpdateAsync(s => s.SetProperty(c => c.ManualRingId, (int?)null), TestContext.Current.CancellationToken));
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var preview = await browser.PostFormAsync("/administration/rules?dialog=new", new Dictionary<string, string>
        {
            ["_handler"] = "rule-dialog",
            ["Mode"] = "create",
            ["Priority"] = "5",
            ["Target"] = $"ring:{RingEntity.PilotId}",
            ["HostNamePattern"] = "*01",
            ["Preview"] = "yes",
        });
        var html = await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("What changes (1 of 2 approved servers)", html, StringComparison.Ordinal);
        Assert.Contains("SQL01</a>", html, StringComparison.Ordinal);
        Assert.Contains(">moves</span>", html, StringComparison.Ordinal);
        Assert.Contains("ring set by hand stays", html, StringComparison.Ordinal);
        Assert.Contains("value=\"*01\"", html, StringComparison.Ordinal);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.AssignmentRules.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Admin_CreatesGroupAndRuleThroughForms()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using (await browser.PostFormAsync("/administration/groups?dialog=new", new Dictionary<string, string> { ["_handler"] = "group-dialog", ["Mode"] = "create", ["Name"] = "SQL servers", ["Windows.0.days"] = "0", ["Windows.0.from"] = "02:00", ["Windows.0.to"] = "05:00", ["Percent"] = "25", ["Max"] = "5" }))
        {
        }

        var groupId = await factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "SQL servers").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        using (await browser.PostFormAsync("/administration/rules?dialog=new", new Dictionary<string, string>
        {
            ["_handler"] = "rule-dialog",
            ["Mode"] = "create",
            ["Priority"] = "5",
            ["Target"] = $"group:{groupId}",
            ["Roles"] = "SqlServer",
        }))
        {
        }

        var rule = await factory.WithDbAsync(db => db.AssignmentRules.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(groupId, rule.TargetId);
        Assert.Equal("SqlServer", rule.Role);

        // Editing the rule: two roles (any of them matches) and a new priority.
        using (var edited = await browser.PostFormAsync($"/administration/rules?dialog=edit&id={rule.Id}", new Dictionary<string, string>
        {
            ["_handler"] = "rule-dialog",
            ["Mode"] = "edit",
            ["Id"] = rule.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Priority"] = "7",
            ["Target"] = $"group:{groupId}",
            ["Roles"] = "ClusterNode",
        }))
        {
            Assert.Contains("The rule was saved.", await edited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var changed = await factory.WithDbAsync(db => db.AssignmentRules.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(7, changed.Priority);
        Assert.Equal("ClusterNode", changed.Role);
    }

    [Theory]
    [InlineData("/administration/groups")]
    [InlineData("/administration/rules")]
    [InlineData("/administration/maintenance")]
    public async Task AdministrationPages_AreOnlyForAdministrators(string path)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
