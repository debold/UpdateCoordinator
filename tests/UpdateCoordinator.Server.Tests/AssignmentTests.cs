using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

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

    private static async Task WithAdministrationAsync(ServerFactory factory, Func<AssignmentAdministration, Task<Outcome>> action)
    {
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        var outcome = await action(scope.ServiceProvider.GetRequiredService<AssignmentAdministration>());
        Assert.True(outcome.Succeeded, outcome.Error);
    }

    private static Task<ClientEntity> GetAsync(ServerFactory factory, Guid clientId) =>
        factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == clientId, TestContext.Current.CancellationToken));

    private static Task<List<int>> GroupsAsync(ServerFactory factory, Guid clientId) =>
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
        Assert.Equal(RingEntity.FallbackId, await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId).Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
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

        using var response = await browser.PostFormAsync($"/servers/{credentials.ClientId}", new Dictionary<string, string>
        {
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

        using var response = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "maintenance", ["Windows"] = "Mon 25:00-01:00" });

        Assert.Contains("Invalid window", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
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

        using (await browser.PostFormAsync("/administration/groups", new Dictionary<string, string> { ["Action"] = "create", ["NewName"] = "SQL servers", ["NewWindows"] = "Sun 02:00-05:00" }))
        {
        }

        var groupId = await factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "SQL servers").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        using (await browser.PostFormAsync("/administration/rules", new Dictionary<string, string>
        {
            ["Action"] = "create",
            ["Priority"] = "5",
            ["Target"] = $"group:{groupId}",
            ["Role"] = "SqlServer",
        }))
        {
        }

        var rule = await factory.WithDbAsync(db => db.AssignmentRules.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(groupId, rule.TargetId);
        Assert.Equal("SqlServer", rule.Role);
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
