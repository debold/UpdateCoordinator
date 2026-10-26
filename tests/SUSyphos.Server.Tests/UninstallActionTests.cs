using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Tests;

/// <summary>Uninstalling an update on one server or, with a block, on all servers that have it.</summary>
public sealed class UninstallActionTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task BlockAndUninstall_QueuesTheUninstallationOnEveryServerThatHasIt()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var first = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var second = await scenario.AddClientAsync("SRV02", RingEntity.PilotId);
        var without = await scenario.AddClientAsync("SRV03", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), KbArticle = "5040434" };
        await scenario.ReportAsync(without, update);
        await InstalledAsync(scenario, first, update.UpdateId);
        await InstalledAsync(scenario, second, update.UpdateId);
        using var admin = await LoginAsync(scenario, AdminRoles.Admin, "admin");
        var path = $"/updates/{update.UpdateId}/1";

        var dialog = await (await admin.GetAsync(path + "?dialog=uninstall")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("the 2 servers that report it as installed", dialog, StringComparison.Ordinal);

        using (var done = await admin.PostFormAndFollowAsync(path + "?dialog=uninstall", new Dictionary<string, string> { ["_handler"] = "uninstall", ["UninstallRestart"] = "window" }))
        {
            Assert.Contains("2 servers uninstall it", await done.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.NotNull(await scenario.Factory.WithDbAsync(db => db.Updates.Where(u => u.UpdateId == update.UpdateId).Select(u => u.BlockedAtUtc).SingleAsync(TestContext.Current.CancellationToken)));
        var actions = await scenario.Factory.WithDbAsync(db => db.AgentActions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a =>
        {
            Assert.Equal(AgentActionKind.UninstallRestartInWindow, a.Kind);
            Assert.Equal(update.UpdateId, a.UpdateId);
            Assert.Equal("5040434", a.KbArticle);
        });

        // The agent gets the update with the action.
        var fetched = Assert.Single(await FetchAsync(scenario, first));
        Assert.Equal((update.UpdateId, (int?)1, "5040434"), (fetched.UpdateId!.Value, fetched.Revision, fetched.KbArticle));
    }

    [Fact]
    public async Task ServerUninstall_NeedsAnAdministratorAndAnInstalledUpdate()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        var notInstalled = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(client, notInstalled);
        await InstalledAsync(scenario, client, update.UpdateId);
        var path = $"/servers/{client.ClientId}";

        using (var operatorBrowser = await LoginAsync(scenario, AdminRoles.Operator, "operator"))
        {
            Assert.DoesNotContain("dialog=uninstall", await (await operatorBrowser.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            using (await operatorBrowser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = "queue:Uninstall" }))
            {
            }
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));

        using var admin = await LoginAsync(scenario, AdminRoles.Admin, "admin");
        var dialog = await (await admin.GetAsync(path + "?dialog=uninstall")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"value=\"{update.UpdateId}\"", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain($"value=\"{notInstalled.UpdateId}\"", dialog, StringComparison.Ordinal);

        // An update the server does not have is refused; the installed one is queued.
        using (await admin.PostFormAsync(path + "?dialog=uninstall", new Dictionary<string, string> { ["_handler"] = "uninstall", ["UninstallRestart"] = "now", ["UninstallUpdate"] = notInstalled.UpdateId.ToString() }))
        {
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
        using (await admin.PostFormAndFollowAsync(path + "?dialog=uninstall", new Dictionary<string, string> { ["_handler"] = "uninstall", ["UninstallRestart"] = "now", ["UninstallUpdate"] = update.UpdateId.ToString() }))
        {
        }

        var action = await scenario.Factory.WithDbAsync(db => db.AgentActions.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentActionKind.Uninstall, action.Kind);
        Assert.Equal(update.UpdateId, action.UpdateId);
    }

    [Fact]
    public async Task ServerUninstall_OffersTheNewestFirst_WithoutSignatures_AndMarksDismOnly()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var signature = Guid.NewGuid();
        var tool = Guid.NewGuid();
        await scenario.Factory.WithDbAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.Updates.AddRange(
                new UpdateEntity { UpdateId = older, Revision = 1, Title = "2026-09 Cumulative Update", KbArticle = "5065432", FirstSeenAtUtc = now },
                new UpdateEntity { UpdateId = newer, Revision = 1, Title = "2026-10 Servicing Stack Update", KbArticle = "5066999", FirstSeenAtUtc = now },
                new UpdateEntity { UpdateId = signature, Revision = 1, Title = "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602", Classification = "Definition Updates", FirstSeenAtUtc = now },
                new UpdateEntity { UpdateId = tool, Revision = 1, Title = "Windows Malicious Software Removal Tool x64 - v5.130", FirstSeenAtUtc = now });
            db.ClientUpdates.AddRange(
                new ClientUpdateEntity { ClientId = client.ClientId, UpdateId = older, Revision = 1, ReportedAtUtc = now, Uninstallable = true },
                new ClientUpdateEntity { ClientId = client.ClientId, UpdateId = newer, Revision = 1, ReportedAtUtc = now, Uninstallable = false },
                new ClientUpdateEntity { ClientId = client.ClientId, UpdateId = signature, Revision = 1, ReportedAtUtc = now },
                new ClientUpdateEntity { ClientId = client.ClientId, UpdateId = tool, Revision = 1, ReportedAtUtc = now });
            db.ClientUpdateHistory.AddRange(
                new ClientUpdateHistoryEntity { ClientId = client.ClientId, UpdateId = older, Revision = 1, Title = "older", DateUtc = now.AddDays(-30), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded },
                new ClientUpdateHistoryEntity { ClientId = client.ClientId, UpdateId = newer, Revision = 1, Title = "newer", DateUtc = now.AddDays(-2), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        using var admin = await LoginAsync(scenario, AdminRoles.Admin, "admin");

        var dialog = await (await admin.GetAsync($"/servers/{client.ClientId}?dialog=uninstall")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(dialog.IndexOf($"value=\"{newer}\"", StringComparison.Ordinal) < dialog.IndexOf($"value=\"{older}\"", StringComparison.Ordinal), "The newest installation comes first.");
        Assert.DoesNotContain($"value=\"{signature}\"", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain($"value=\"{tool}\"", dialog, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(dialog, "only through DISM, may fail"));
        Assert.Contains("type=\"radio\"", dialog, StringComparison.Ordinal);
    }

    private static Task<int> InstalledAsync(RolloutScenario scenario, RegisterResponse client, Guid updateId) =>
        scenario.Factory.WithDbAsync(async db =>
        {
            if (!await db.Updates.AnyAsync(u => u.UpdateId == updateId, TestContext.Current.CancellationToken))
            {
                db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = 1, Title = "Installed update", KbArticle = "5040434", FirstSeenAtUtc = DateTime.UtcNow });
            }

            db.ClientUpdates.Add(new ClientUpdateEntity { ClientId = client.ClientId, UpdateId = updateId, Revision = 1, ReportedAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    private static async Task<IReadOnlyList<AgentAction>> FetchAsync(RolloutScenario scenario, RegisterResponse client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentActions, UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentActionList>(TestContext.Current.CancellationToken))!.Actions;
    }

    private static async Task<SUSyphos.TestSupport.DashboardClient> LoginAsync(RolloutScenario scenario, string role, string user)
    {
        await scenario.Factory.CreateUserAsync(user, Password, role);
        var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync(user, Password))
        {
        }

        return browser;
    }
}
