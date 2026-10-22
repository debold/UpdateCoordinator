using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Notifications;

namespace SUSyphos.Server.Tests;

/// <summary>Updates Microsoft withdraws, and updates an operator blocks.</summary>
public sealed class WithdrawnUpdateTests
{
    [Fact]
    public async Task UpdateNoServerKnowsAnyMore_StopsTheRollout_AndNotifies()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        Assert.Single((await scenario.ReleasesAsync(pilot)).Releases);

        // The next search no longer finds it, and the lookup by ID says Windows Update does not know it.
        await ReportAsync(scenario, pilot, [], [new UpdateKey(update.UpdateId, 1)]);

        Assert.Equal(RolloutState.Stopped, await StateAsync(scenario, update.UpdateId));
        Assert.NotNull(await WithdrawnAsync(scenario, update.UpdateId));
        Assert.Empty((await scenario.ReleasesAsync(pilot)).Releases);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Rollout.Withdrawn", TestContext.Current.CancellationToken)));
        await using var scope = scenario.Factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<NotificationEvents>().CollectAsync(TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Kind == NotificationKind.UpdateWithdrawn && e.Text.Contains("rollout is stopped", StringComparison.Ordinal));

        // Offered again: the mark goes away, the rollout stays stopped until an operator resumes it.
        await ReportAsync(scenario, pilot, [update], []);
        Assert.Null(await WithdrawnAsync(scenario, update.UpdateId));
        Assert.Equal(RolloutState.Stopped, await StateAsync(scenario, update.UpdateId));
    }

    [Fact]
    public async Task OnlyNotify_KeepsTheRolloutRunning()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.SetStopWithdrawnAsync(false, "admin", CancellationToken.None))).Succeeded);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);

        await ReportAsync(scenario, pilot, [], [new UpdateKey(update.UpdateId, 1)]);

        Assert.NotNull(await WithdrawnAsync(scenario, update.UpdateId));
        Assert.Equal(RolloutState.Active, await StateAsync(scenario, update.UpdateId));
    }

    [Fact]
    public async Task AnotherServerStillGetsItOffered_NothingHappens()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var other = await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        await scenario.ReportAsync(other, update);

        await ReportAsync(scenario, pilot, [], [new UpdateKey(update.UpdateId, 1)]);

        Assert.Null(await WithdrawnAsync(scenario, update.UpdateId));
        Assert.Equal(RolloutState.Active, await StateAsync(scenario, update.UpdateId));
    }

    [Fact]
    public async Task NewRevisionOrSupersedingUpdate_IsNoWithdrawal()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var revised = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        var replaced = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, revised, replaced);

        // Revision 2 of the first and a successor of the second are offered instead.
        var successor = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), SupersededUpdateIds = [replaced.UpdateId] };
        await ReportAsync(scenario, pilot, [revised with { Revision = 2 }, successor], [new UpdateKey(revised.UpdateId, 1), new UpdateKey(replaced.UpdateId, 1)]);

        Assert.Null(await WithdrawnAsync(scenario, revised.UpdateId));
        Assert.Null(await WithdrawnAsync(scenario, replaced.UpdateId));
    }

    [Fact]
    public async Task BlockedUpdate_StopsAndNeverStartsAgainUntilUnblocked()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);

        Assert.True((await scenario.AdministerAsync(admin => admin.BlockAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);
        Assert.Equal(RolloutState.Stopped, await StateAsync(scenario, update.UpdateId));
        Assert.Empty((await scenario.ReleasesAsync(pilot)).Releases);

        Assert.False((await scenario.AdministerAsync(admin => admin.BlockAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.StartAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.OverrideAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);

        Assert.True((await scenario.AdministerAsync(admin => admin.UnblockAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.UnblockAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);
        Assert.Equal(RolloutState.Stopped, await StateAsync(scenario, update.UpdateId));
        Assert.True((await scenario.AdministerAsync(admin => admin.StartAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);
        Assert.Equal(2, await scenario.Factory.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action.StartsWith("Update."), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task BlockedQueuedUpdate_IsNotStartedByARule()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.ManualStartOnlyAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update);
        Assert.True((await scenario.AdministerAsync(admin => admin.BlockAsync(update.UpdateId, 1, "alice", CancellationToken.None))).Succeeded);

        // A rule that would start everything now.
        Assert.True((await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(5, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None))).Succeeded);
        await scenario.ReportAsync(pilot, update);

        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == update.UpdateId, TestContext.Current.CancellationToken)));
    }

    private static async Task ReportAsync(RolloutScenario scenario, RegisterResponse client, AvailableUpdate[] available, UpdateKey[] notOffered)
    {
        var report = UpdateReportTests.SuccessfulReport(available) with { NotOffered = notOffered };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentUpdateReport, UriKind.Relative)) { Content = JsonContent.Create(report) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static Task<RolloutState> StateAsync(RolloutScenario scenario, Guid updateId) =>
        scenario.Factory.WithDbAsync(db => db.Rollouts.Where(r => r.UpdateId == updateId).Select(r => r.State).SingleAsync(TestContext.Current.CancellationToken));

    private static Task<DateTime?> WithdrawnAsync(RolloutScenario scenario, Guid updateId) =>
        scenario.Factory.WithDbAsync(db => db.Updates.Where(u => u.UpdateId == updateId && u.Revision == 1).Select(u => u.WithdrawnAtUtc).SingleAsync(TestContext.Current.CancellationToken));
}
