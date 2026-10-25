using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class RebootPolicyTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Policy_IsInheritedSystemThenGroupsThenServer_AndDelivered()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var initial = await scenario.ReleasesAsync(client);
        Assert.Equal(RebootPolicy.Never, initial.RebootPolicy);

        await AdministerAsync(scenario, a => a.SetSystemRebootPolicyAsync(RebootPolicy.InsideMaintenanceWindow, "admin", CancellationToken.None));
        var afterSystem = await scenario.ReleasesAsync(client);
        Assert.Equal(RebootPolicy.InsideMaintenanceWindow, afterSystem.RebootPolicy);
        Assert.True(afterSystem.Version > initial.Version);

        await AdministerAsync(scenario, a => a.CreateGroupAsync("Domain controllers", null, null, "admin", CancellationToken.None));
        await AdministerAsync(scenario, a => a.CreateGroupAsync("Web", null, null, "admin", CancellationToken.None));
        var dc = await scenario.Factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "Domain controllers").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        var web = await scenario.Factory.WithDbAsync(db => db.Groups.Where(g => g.Name == "Web").Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        await AdministerAsync(scenario, a => a.SetGroupRebootPolicyAsync(dc, RebootPolicy.Never, "admin", CancellationToken.None));
        await AdministerAsync(scenario, a => a.SetGroupRebootPolicyAsync(web, RebootPolicy.InsideMaintenanceWindow, "admin", CancellationToken.None));
        await AdministerAsync(scenario, a => a.SetClientAssignmentAsync(client.ClientId, RingEntity.PilotId, [dc, web], null, "admin", CancellationToken.None));

        // Among groups the most restrictive policy wins.
        Assert.Equal(RebootPolicy.Never, (await scenario.ReleasesAsync(client)).RebootPolicy);

        await AdministerAsync(scenario, a => a.SetClientRebootPolicyAsync(client.ClientId, RebootPolicy.InsideMaintenanceWindow, "admin", CancellationToken.None));
        Assert.Equal(RebootPolicy.InsideMaintenanceWindow, (await scenario.ReleasesAsync(client)).RebootPolicy);

        await AdministerAsync(scenario, a => a.SetClientRebootPolicyAsync(client.ClientId, null, "admin", CancellationToken.None));
        Assert.Equal(RebootPolicy.Never, (await scenario.ReleasesAsync(client)).RebootPolicy);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action.Contains("Reboot"), TestContext.Current.CancellationToken)) >= 5);
    }

    [Fact]
    public async Task UndefinedPoliciesAndUnknownTargets_AreRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetSystemRebootPolicyAsync((RebootPolicy)7, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupRebootPolicyAsync(99, RebootPolicy.Never, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientRebootPolicyAsync(Guid.NewGuid(), RebootPolicy.Never, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientRebootPolicyAsync(client.ClientId, (RebootPolicy)(-1), "admin", CancellationToken.None))).Succeeded);
        Assert.Equal(RebootPolicy.Never, (await scenario.ReleasesAsync(client)).RebootPolicy);
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("Never", true, RebootPolicy.Never)]
    [InlineData("InsideMaintenanceWindow", true, RebootPolicy.InsideMaintenanceWindow)]
    [InlineData("1", false, null)]
    [InlineData("never", false, null)]
    [InlineData("Always", false, null)]
    [InlineData("<script>", false, null)]
    public void FormValues_AreParsedStrictly(string value, bool valid, RebootPolicy? expected)
    {
        Assert.Equal(valid, RebootPolicyText.TryParse(value, out var policy));
        Assert.Equal(expected, policy);
    }

    [Fact]
    public async Task Heartbeat_StoresPendingRebootAndBootTime_IgnoringImplausibleBoot()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var boot = DateTimeOffset.UtcNow.AddHours(-3);

        await HeartbeatAsync(scenario, client, rebootPending: true, boot);
        var stored = await GetClientAsync(scenario);
        Assert.True(stored.RebootPending);
        Assert.Equal(boot.UtcDateTime, stored.LastBootAtUtc!.Value, TimeSpan.FromSeconds(1));

        await HeartbeatAsync(scenario, client, rebootPending: false, DateTimeOffset.UtcNow.AddDays(3));
        stored = await GetClientAsync(scenario);
        Assert.False(stored.RebootPending);
        Assert.Equal(boot.UtcDateTime, stored.LastBootAtUtc!.Value, TimeSpan.FromSeconds(1));

        await HeartbeatAsync(scenario, client, rebootPending: false, new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero));
        stored = await GetClientAsync(scenario);
        Assert.Equal(boot.UtcDateTime, stored.LastBootAtUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Admin_SetsSystemPolicyThroughForm_AndInvalidValueIsRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        using var browser = await LoginAsync(scenario, AdminRoles.Admin);

        using (var saved = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "reboot", ["Reboot"] = "InsideMaintenanceWindow" }))
        {
            Assert.Contains("The reboot policy was saved", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var refused = await browser.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "reboot", ["Reboot"] = "" }))
        {
            Assert.Contains("The reboot policy is invalid.", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(RebootPolicy.InsideMaintenanceWindow, await scenario.WithServiceAsync<AssignmentService, RebootPolicy>(s => s.GetSystemRebootPolicyAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Admin_SetsGroupAndServerPolicyThroughForms()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await AdministerAsync(scenario, a => a.CreateGroupAsync("Web", null, null, "admin", CancellationToken.None));
        var group = await scenario.Factory.WithDbAsync(db => db.Groups.Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        using var browser = await LoginAsync(scenario, AdminRoles.Admin);

        using (await browser.PostFormAsync($"/administration/groups?dialog=edit&id={group}", new Dictionary<string, string> { ["_handler"] = "group-dialog", ["Mode"] = "edit", ["Id"] = group.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Windows.inherit"] = "yes", ["Reboot"] = "InsideMaintenanceWindow", ["Percent"] = "25", ["Max"] = "5" }))
        {
        }

        using (var refused = await browser.PostFormAsync($"/administration/groups?dialog=edit&id={group}", new Dictionary<string, string> { ["_handler"] = "group-dialog", ["Mode"] = "edit", ["Id"] = group.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Reboot"] = "Always", ["Percent"] = "25", ["Max"] = "5" }))
        {
            Assert.Contains("The reboot policy is invalid.", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (await browser.PostFormAsync($"/servers/{client.ClientId}?tab=configuration", new Dictionary<string, string> { ["_handler"] = "assignment", ["Ring"] = "", ["Reboot"] = "Never" }))
        {
        }

        Assert.Equal(RebootPolicy.InsideMaintenanceWindow, await scenario.Factory.WithDbAsync(db => db.Groups.Select(g => g.RebootPolicy).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(RebootPolicy.Never, await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.RebootPolicy).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Reader_CannotChangeServerPolicy_AndSeesRestartInHistory()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        await scenario.InstallAsync(client, update.UpdateId, InstallResultCode.Succeeded);
        await scenario.Factory.WithDbAsync(db => db.Installations.ExecuteUpdateAsync(s => s.SetProperty(i => i.RebootRequired, true), TestContext.Current.CancellationToken));
        await HeartbeatAsync(scenario, client, rebootPending: false, DateTimeOffset.UtcNow);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        using var page = await browser.GetAsync($"/servers/{client.ClientId}?tab=history");
        using (await browser.PostFormAsync($"/servers/{client.ClientId}?tab=configuration", new Dictionary<string, string> { ["_handler"] = "assignment", ["Reboot"] = "InsideMaintenanceWindow" }, pageToRead: "/"))
        {
        }

        Assert.Contains("<span class=\"pill ok\">Restarted</span>", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Null(await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.RebootPolicy).SingleAsync(TestContext.Current.CancellationToken)));
    }

    private static Task<ClientEntity> GetClientAsync(RolloutScenario scenario) =>
        scenario.Factory.WithDbAsync(db => db.Clients.SingleAsync(TestContext.Current.CancellationToken));

    private static async Task AdministerAsync(RolloutScenario scenario, Func<AssignmentAdministration, Task<Outcome>> action)
    {
        var outcome = await scenario.WithServiceAsync(action);
        Assert.True(outcome.Succeeded, outcome.Error);
    }

    private static async Task HeartbeatAsync(RolloutScenario scenario, RegisterResponse client, bool rebootPending, DateTimeOffset lastBoot)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0", RebootPending = rebootPending, LastBootAt = lastBoot }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
