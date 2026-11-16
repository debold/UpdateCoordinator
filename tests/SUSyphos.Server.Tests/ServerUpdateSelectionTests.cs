using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Tests;

/// <summary>
/// Actions on a selection of the updates of one server (Marc, 2026-11-16): install now (an update still waiting for the
/// ring is released for the server alone), hold, end a hold and uninstall, each skipping what it does not apply to.
/// </summary>
public sealed class ServerUpdateSelectionTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task InstallNow_ReleasesWaitingUpdatesForTheServerAlone_AndNamesOnlyTheSelection()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.ProductionId);
        var other = await scenario.AddClientAsync("SRV02", RingEntity.ProductionId);
        await AgentVersionAsync(scenario, client, AgentAction.SelectionSinceVersion);
        var chosen = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        var notChosen = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(client, chosen, notChosen);
        await scenario.ReportAsync(other, chosen);

        // Production waits for the earlier rings: nothing is released for the server yet.
        Assert.Empty((await scenario.ReleasesAsync(client)).Releases);

        var result = await SelectionAsync(scenario, s => s.InstallAsync(client.ClientId, [chosen.UpdateId], AgentActionKind.InstallNowRestartInWindow, "admin", CancellationToken.None));

        Assert.Null(result.Error);
        Assert.Equal(1, result.Done);
        Assert.Equal(chosen.UpdateId, Assert.Single((await scenario.ReleasesAsync(client)).Releases).UpdateId);
        Assert.Empty((await scenario.ReleasesAsync(other)).Releases);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Update.ReleasedForServer", TestContext.Current.CancellationToken)));

        var action = Assert.Single(await FetchAsync(scenario, client));
        Assert.Equal(AgentActionKind.InstallNowRestartInWindow, action.Kind);
        Assert.Equal([chosen.UpdateId], action.UpdateIds);

        // Once the server reports it installed, the release for the server alone ends.
        await scenario.InstallAsync(client, chosen.UpdateId, InstallResultCode.Succeeded);
        await scenario.ReportAsync(client, notChosen);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.ServerReleases.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task InstallNow_RefusesAnOldAgent_AndAnEmptyOrOversizedSelection()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await AgentVersionAsync(scenario, client, "0.9.0-rc.4");
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(client, update);

        var old = await SelectionAsync(scenario, s => s.InstallAsync(client.ClientId, [update.UpdateId], AgentActionKind.InstallNow, "admin", CancellationToken.None));
        Assert.Contains(AgentAction.SelectionSinceVersion, old.Error, StringComparison.Ordinal);

        await AgentVersionAsync(scenario, client, "0.9.0");
        var empty = await SelectionAsync(scenario, s => s.InstallAsync(client.ClientId, [], AgentActionKind.InstallNow, "admin", CancellationToken.None));
        var oversized = await SelectionAsync(scenario, s => s.InstallAsync(client.ClientId, [.. Enumerable.Range(0, ServerUpdateSelection.MaxSelection + 1).Select(_ => Guid.NewGuid())], AgentActionKind.InstallNow, "admin", CancellationToken.None));
        var unknown = await SelectionAsync(scenario, s => s.InstallAsync(client.ClientId, [Guid.NewGuid()], AgentActionKind.InstallNow, "admin", CancellationToken.None));
        var wrongKind = await SelectionAsync(scenario, s => s.InstallAsync(client.ClientId, [update.UpdateId], AgentActionKind.Uninstall, "admin", CancellationToken.None));

        Assert.NotNull(empty.Error);
        Assert.NotNull(oversized.Error);
        Assert.NotNull(unknown.Error);
        Assert.Equal("No longer listed for this server.", Assert.Single(unknown.Skipped).Reason);
        Assert.NotNull(wrongKind.Error);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.False(await scenario.Factory.WithDbAsync(db => db.ServerReleases.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Hold_KeepsTheUpdateAway_InstallSkipsIt_AndEndHoldReleasesItAgain()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await AgentVersionAsync(scenario, client, "0.9.1");
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(client, update);
        Assert.Single((await scenario.ReleasesAsync(client)).Releases);

        var held = await SelectionAsync(scenario, s => s.HoldAsync(client.ClientId, [update.UpdateId], [], "ticket 42", null, "operator", CancellationToken.None));
        Assert.Equal(1, held.Done);
        Assert.Empty((await scenario.ReleasesAsync(client)).Releases);

        var again = await SelectionAsync(scenario, s => s.HoldAsync(client.ClientId, [update.UpdateId], [], null, null, "operator", CancellationToken.None));
        Assert.Equal("Already on hold for this server.", Assert.Single(again.Skipped).Reason);

        var install = await SelectionAsync(scenario, s => s.InstallAsync(client.ClientId, [update.UpdateId], AgentActionKind.InstallNow, "admin", CancellationToken.None));
        Assert.Equal(0, install.Done);
        Assert.StartsWith("On hold for this server", Assert.Single(install.Skipped).Reason, StringComparison.Ordinal);

        var ended = await SelectionAsync(scenario, s => s.EndHoldAsync(client.ClientId, [update.UpdateId], "operator", CancellationToken.None));
        Assert.Equal(1, ended.Done);
        Assert.Single((await scenario.ReleasesAsync(client)).Releases);
    }

    [Fact]
    public async Task Hold_RefusesABadReasonOrAPastEnd_AndEndHoldLeavesTheHoldOfTheRing()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(client, update);

        var badReason = await SelectionAsync(scenario, s => s.HoldAsync(client.ClientId, [update.UpdateId], [], "line\nbreak", null, "operator", CancellationToken.None));
        var past = await SelectionAsync(scenario, s => s.HoldAsync(client.ClientId, [update.UpdateId], [], null, DateTime.UtcNow.AddHours(-1), "operator", CancellationToken.None));
        Assert.NotNull(badReason.Error);
        Assert.NotNull(past.Error);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.UpdateHolds.AnyAsync(TestContext.Current.CancellationToken)));

        Assert.True((await scenario.WithServiceAsync<UpdateHolds, SUSyphos.Server.Administration.Outcome>(h => h.HoldAsync(update.UpdateId, 1, HoldTarget.Ring, RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, "admin", CancellationToken.None))).Succeeded);
        var end = await SelectionAsync(scenario, s => s.EndHoldAsync(client.ClientId, [update.UpdateId], "operator", CancellationToken.None));
        Assert.Equal(0, end.Done);
        Assert.Contains("end that hold on the update page", Assert.Single(end.Skipped).Reason, StringComparison.Ordinal);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.UpdateHolds.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Uninstall_QueuesOneActionEach_AndCanHoldTheUpdatesForTheServer()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await InstalledAsync(scenario, client, first, "5040001");
        await InstalledAsync(scenario, client, second, "5040002");

        var result = await SelectionAsync(scenario, s => s.UninstallAsync(client.ClientId, [$"u:{first:D}", $"u:{second:D}", "u:not-a-guid", "p:Contoso.Missing"], AgentActionKind.UninstallInWindow, true, "admin", CancellationToken.None));

        Assert.Equal(2, result.Done);
        Assert.Single(result.Skipped);
        var actions = await scenario.Factory.WithDbAsync(db => db.AgentActions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([first, second], actions.Select(a => a.UpdateId!.Value).Order());
        Assert.All(actions, a => Assert.Equal(AgentActionKind.UninstallInWindow, a.Kind));
        Assert.Equal(2, await scenario.Factory.WithDbAsync(db => db.UpdateHolds.CountAsync(h => h.ClientId == client.ClientId, TestContext.Current.CancellationToken)));

        // The same selection again: their uninstallations already wait.
        var again = await SelectionAsync(scenario, s => s.UninstallAsync(client.ClientId, [$"u:{first:D}"], AgentActionKind.Uninstall, false, "admin", CancellationToken.None));
        Assert.Equal(0, again.Done);
        Assert.Equal("Its uninstallation already waits on the server.", Assert.Single(again.Skipped).Reason);

        var wrongKind = await SelectionAsync(scenario, s => s.UninstallAsync(client.ClientId, [$"u:{first:D}"], AgentActionKind.InstallNow, false, "admin", CancellationToken.None));
        Assert.NotNull(wrongKind.Error);
    }

    [Fact]
    public async Task UpdatesTab_OffersTheSelectionByRole_AndTheDialogNamesWhatIsSkipped()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await AgentVersionAsync(scenario, client, AgentAction.SelectionSinceVersion);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        var installed = Guid.NewGuid();
        await scenario.ReportAsync(client, update);
        await InstalledAsync(scenario, client, installed, "5040003");
        var path = $"/servers/{client.ClientId}";

        using (var reader = await LoginAsync(scenario, SUSyphos.Server.Administration.AdminRoles.Reader, "reader"))
        {
            var tab = await (await reader.GetAsync(path + "?tab=updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("name=\"sel\"", tab, StringComparison.Ordinal);
            Assert.DoesNotContain("name=\"isel\"", tab, StringComparison.Ordinal);
        }

        using (var op = await LoginAsync(scenario, SUSyphos.Server.Administration.AdminRoles.Operator, "operator"))
        {
            var tab = await (await op.GetAsync(path + "?tab=updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains($"name=\"sel\" value=\"{update.UpdateId}\"", tab, StringComparison.Ordinal);
            Assert.DoesNotContain("name=\"isel\"", tab, StringComparison.Ordinal);

            // An operator may hold, but not install outside the window.
            using (await op.PostFormAsync($"{path}?tab=updates&dialog=install-selection&sel={update.UpdateId}", new Dictionary<string, string> { ["_handler"] = "install-selection", ["Selected"] = update.UpdateId.ToString(), ["SelectionRestart"] = "now" }))
            {
            }

            Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
            using (await op.PostFormAndFollowAsync($"{path}?tab=updates&dialog=hold-selection&sel={update.UpdateId}", new Dictionary<string, string> { ["_handler"] = "hold-selection", ["Selected"] = update.UpdateId.ToString(), ["HoldReason"] = "change freeze" }))
            {
            }

            Assert.Equal("change freeze", await scenario.Factory.WithDbAsync(db => db.UpdateHolds.Select(h => h.Reason).SingleAsync(TestContext.Current.CancellationToken)));
        }

        using var admin = await LoginAsync(scenario, SUSyphos.Server.Administration.AdminRoles.Admin, "admin");
        var adminTab = await (await admin.GetAsync(path + "?tab=updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"name=\"isel\" value=\"u:{installed}\"", adminTab, StringComparison.Ordinal);
        Assert.Contains("on hold for this server", adminTab, StringComparison.Ordinal);

        // The held update is named as skipped, a malformed entry is ignored; nothing to install is left.
        var dialog = System.Net.WebUtility.HtmlDecode(await (await admin.GetAsync($"{path}?tab=updates&dialog=install-selection&sel={update.UpdateId}&sel=%27%3Bdrop")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("On hold for this server; end the hold first.", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain($"name=\"Selected\" value=\"{update.UpdateId}\"", dialog, StringComparison.Ordinal);

        var nothing = await (await admin.GetAsync($"{path}?tab=updates&dialog=install-selection")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Nothing is selected.", nothing, StringComparison.Ordinal);

        // After the hold ends, the admin installs the selection through the dialog.
        using (await admin.PostFormAndFollowAsync($"{path}?tab=updates&dialog=end-hold-selection&sel={update.UpdateId}", new Dictionary<string, string> { ["_handler"] = "end-hold-selection", ["Selected"] = update.UpdateId.ToString() }))
        {
        }

        using (await admin.PostFormAndFollowAsync($"{path}?tab=updates&dialog=install-selection&sel={update.UpdateId}", new Dictionary<string, string> { ["_handler"] = "install-selection", ["Selected"] = update.UpdateId.ToString(), ["SelectionRestart"] = "now" }))
        {
        }

        var action = await scenario.Factory.WithDbAsync(db => db.AgentActions.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentActionKind.InstallNow, action.Kind);
        Assert.Equal(update.UpdateId, await scenario.Factory.WithDbAsync(db => db.AgentActionItems.Where(i => i.ActionId == action.Id).Select(i => i.UpdateId).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("0.9.0-rc.5", true)]
    [InlineData("0.9.0", true)]
    [InlineData("1.0.0+abc", true)]
    [InlineData("0.9.0-rc.4", false)]
    [InlineData("0.9.0-rc.4.12", false)]
    [InlineData("0.8.9", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SupportsSelection_FromTheFirstAgentThatInstallsASelection(string? version, bool expected) =>
        Assert.Equal(expected, SUSyphos.Server.Agents.AgentActionService.SupportsSelection(version));

    private static Task<int> AgentVersionAsync(RolloutScenario scenario, RegisterResponse client, string version) =>
        scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == client.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AgentVersion, version), TestContext.Current.CancellationToken));

    private static Task<SelectionResult> SelectionAsync(RolloutScenario scenario, Func<ServerUpdateSelection, Task<SelectionResult>> action) =>
        scenario.WithServiceAsync(action);

    private static Task<int> InstalledAsync(RolloutScenario scenario, RegisterResponse client, Guid updateId, string kb) =>
        scenario.Factory.WithDbAsync(async db =>
        {
            db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = 1, Title = $"Installed update (KB{kb})", KbArticle = kb, Classification = "Security Updates", FirstSeenAtUtc = DateTime.UtcNow });
            db.ClientUpdates.Add(new ClientUpdateEntity { ClientId = client.ClientId, UpdateId = updateId, Revision = 1, ReportedAtUtc = DateTime.UtcNow, Uninstallable = true });
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
