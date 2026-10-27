using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Notifications;

namespace SUSyphos.Server.Tests;

/// <summary>Results of manual remote actions where operators see them (Marc, 2026-10-27).</summary>
public sealed class ActionResultTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task FailedAndFinishedActions_WaitOnTheApprovalsPage_UntilAcknowledged()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var failed = await FinishedAsync(scenario, client.ClientId, AgentActionKind.Uninstall, AgentActionState.Rejected, "No installed package of KB5128923 was found (DISM).");
        await FinishedAsync(scenario, client.ClientId, AgentActionKind.UploadLogs, AgentActionState.Succeeded, "4 log files uploaded (313 KB).");
        await FinishedAsync(scenario, client.ClientId, AgentActionKind.SearchNow, AgentActionState.Succeeded, "The update search runs with this cycle.");
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        var open = await scenario.WithServiceAsync<ActionResults, List<ActionResult>>(r => r.OpenAsync(CancellationToken.None));
        Assert.Equal([AgentActionKind.Uninstall, AgentActionKind.UploadLogs], open.Select(r => r.Kind));
        Assert.Equal(1, await scenario.WithServiceAsync<ActionResults, int>(r => r.OpenFailuresAsync(CancellationToken.None)));

        var page = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync("/approvals")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("No installed package of KB5128923 was found (DISM).", page, StringComparison.Ordinal);
        Assert.Contains("4 log files uploaded (313 KB).", page, StringComparison.Ordinal);
        Assert.DoesNotContain("The update search runs with this cycle.", page, StringComparison.Ordinal);

        using (await browser.PostFormAndFollowAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "action-results", ["Acknowledge"] = failed.ToString() }))
        {
        }

        Assert.Equal([AgentActionKind.UploadLogs], (await scenario.WithServiceAsync<ActionResults, List<ActionResult>>(r => r.OpenAsync(CancellationToken.None))).Select(r => r.Kind));
        using (await browser.PostFormAndFollowAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "action-results", ["Acknowledge"] = "all" }))
        {
        }

        Assert.Empty(await scenario.WithServiceAsync<ActionResults, List<ActionResult>>(r => r.OpenAsync(CancellationToken.None)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Action.Acknowledged", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Readers_CannotAcknowledge()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await FinishedAsync(scenario, client.ClientId, AgentActionKind.RestartServer, AgentActionState.Failed, "veto");
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        using (await browser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "action-results", ["Acknowledge"] = "all" }, pageToRead: "/approvals"))
        {
        }

        Assert.Single(await scenario.WithServiceAsync<ActionResults, List<ActionResult>>(r => r.OpenAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Notifications_TellFailedAndFinishedActions()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await FinishedAsync(scenario, client.ClientId, AgentActionKind.Uninstall, AgentActionState.Rejected, "DISM refused");
        await FinishedAsync(scenario, client.ClientId, AgentActionKind.UploadLogs, AgentActionState.Succeeded, "uploaded");
        await FinishedAsync(scenario, client.ClientId, AgentActionKind.SearchNow, AgentActionState.Succeeded, "runs");

        var events = await scenario.WithServiceAsync<NotificationEvents, List<NotificationEvent>>(e => e.CollectAsync(CancellationToken.None));

        Assert.Contains(events, e => e.Kind == NotificationKind.RemoteActionFailed && e.Text.Contains("DISM refused", StringComparison.Ordinal));
        Assert.Single(events, e => e.Kind == NotificationKind.RemoteActionFinished);
        Assert.Contains(NotificationKind.RemoteActionFailed, NotificationCatalog.Defaults);
        Assert.DoesNotContain(NotificationKind.RemoteActionFinished, NotificationCatalog.Defaults);
    }

    [Fact]
    public async Task ServerPage_OffersTheNewestLogs_InTheActionBand()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        Assert.DoesNotContain("Download logs", await (await browser.GetAsync($"/servers/{client.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var logs = await FinishedAsync(scenario, client.ClientId, AgentActionKind.UploadLogs, AgentActionState.Succeeded, "4 log files uploaded (313 KB).");
        await scenario.Factory.WithDbAsync(db => db.AgentActions.Where(a => a.Id == logs).ExecuteUpdateAsync(s => s.SetProperty(a => a.LogFileSize, 320_000L), TestContext.Current.CancellationToken));

        var page = await (await browser.GetAsync($"/servers/{client.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Download logs", page, StringComparison.Ordinal);
        Assert.Contains(AgentLogEndpoints.DownloadPath(logs), page, StringComparison.Ordinal);
    }

    private static async Task<Guid> FinishedAsync(RolloutScenario scenario, Guid clientId, AgentActionKind kind, AgentActionState state, string detail)
    {
        var id = Guid.NewGuid();
        await scenario.Factory.WithDbAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.AgentActions.Add(new AgentActionEntity { Id = id, ClientId = clientId, Kind = kind, State = state, RequestedAtUtc = now.AddMinutes(-5), RequestedBy = "admin", DeliveredAtUtc = now.AddMinutes(-4), CompletedAtUtc = now, Detail = detail });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        return id;
    }
}
