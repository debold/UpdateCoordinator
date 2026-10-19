using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.Server.Rollouts;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class TransitionRuleTests
{
    private static readonly DateTime Released = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly RingTransitionEntity Rule = new()
    {
        FromRingId = RingEntity.PilotId,
        MinimumWait = TimeSpan.FromDays(2),
        MaximumWait = TimeSpan.FromDays(7),
        MinimumFeedbackPercent = 80,
        MinimumSuccessPercent = 95,
    };

    [Theory]
    [InlineData(1, 10, 10, 10, (int)TransitionDecision.Wait)] // before the minimum wait time
    [InlineData(2, 0, 0, 0, (int)TransitionDecision.Proceed)] // no server needs it
    [InlineData(2, 10, 8, 8, (int)TransitionDecision.Proceed)] // exactly 80 % feedback, 100 % success
    [InlineData(2, 20, 20, 19, (int)TransitionDecision.Proceed)] // exactly 95 % success
    [InlineData(2, 20, 20, 18, (int)TransitionDecision.Alarm)] // 90 % success with enough feedback
    [InlineData(3, 10, 7, 7, (int)TransitionDecision.Wait)] // 70 % feedback before the maximum wait time
    [InlineData(7, 10, 7, 7, (int)TransitionDecision.Proceed)] // maximum wait time, good success rate
    [InlineData(7, 10, 0, 0, (int)TransitionDecision.Alarm)] // maximum wait time, no feedback at all
    [InlineData(7, 10, 5, 3, (int)TransitionDecision.Alarm)] // maximum wait time, poor success rate
    public void Decision_FollowsRatesAndWaitTimes(int days, int targeted, int reported, int succeeded, int expected)
    {
        var (decision, reason) = TransitionRule.Evaluate(Rule, new RingStatistics(targeted, reported, succeeded), Released, Released.AddDays(days));

        Assert.Equal((TransitionDecision)expected, decision);
        Assert.Equal(decision == TransitionDecision.Alarm, reason is not null);
    }

    [Fact]
    public void Statistics_ReportRatesSeparately()
    {
        var statistics = new RingStatistics(10, 4, 3);

        Assert.Equal(40, statistics.FeedbackPercent);
        Assert.Equal(75, statistics.SuccessPercent);
        Assert.Null(new RingStatistics(0, 0, 0).FeedbackPercent);
        Assert.Null(new RingStatistics(3, 0, 0).SuccessPercent);
    }
}

public sealed class StartRuleTests
{
    private static readonly UpdateEntity Cumulative = new()
    {
        UpdateId = Guid.NewGuid(),
        Revision = 1,
        Title = "2026-10 Cumulative Update for Microsoft server operating system",
        Classification = "Security Updates",
        MsrcSeverity = "Critical",
    };

    private static StartRuleEntity Rule(int id, int priority, string? classification = null, string? severity = null, string? title = null) =>
        new() { Id = id, Priority = priority, Classification = classification, MsrcSeverity = severity, TitlePattern = title };

    [Fact]
    public void LowestPriorityMatchingRule_Wins()
    {
        var rules = new[] { Rule(1, 1000), Rule(2, 20, "security updates"), Rule(3, 10, "Feature Packs") };

        Assert.Equal(2, RolloutService.FindStartRule(rules, Cumulative)!.Id);
    }

    [Theory]
    [InlineData(null, "Critical", null, true)]
    [InlineData(null, "Important", null, false)]
    [InlineData(null, null, "*cumulative update*", true)]
    [InlineData(null, null, "Cumulative*", false)]
    [InlineData("Security Updates", "Critical", "*Cumulative*", true)]
    [InlineData("Security Updates", "Low", "*Cumulative*", false)]
    public void AllConditions_MustMatch(string? classification, string? severity, string? title, bool matches)
    {
        Assert.Equal(matches, RolloutService.FindStartRule([Rule(1, 1, classification, severity, title)], Cumulative) is not null);
    }

    [Fact]
    public void UpdateWithoutClassification_MatchesOnlyRulesWithoutClassification()
    {
        var update = new UpdateEntity { UpdateId = Guid.NewGuid(), Revision = 1, Title = "Unknown" };

        Assert.Null(RolloutService.FindStartRule([Rule(1, 1, "Security Updates")], update));
        Assert.NotNull(RolloutService.FindStartRule([Rule(1, 1)], update));
    }
}

public sealed class RolloutTests
{
    [Fact]
    public async Task NewUpdate_StartsAutomaticallyByDefault_AndIsReleasedToPilotOnly()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        var update = RolloutScenario.Update("Security Updates");

        await scenario.ReportAsync(pilot, update);
        await scenario.ReportAsync(production, update);

        var rollout = await scenario.Factory.WithDbAsync(db => db.Rollouts.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(RolloutService.RuleActor, rollout.StartedBy);
        Assert.Equal(update.UpdateId, Assert.Single((await scenario.ReleasesAsync(pilot)).Releases).UpdateId);
        Assert.Empty((await scenario.ReleasesAsync(production)).Releases);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Rollout.Started" && e.Actor == "rule", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ManualStartRule_KeepsMatchingUpdatesQueued()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(10, "Feature Packs", null, null, StartMode.Manual, 0, "admin", CancellationToken.None))).Succeeded);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var security = RolloutScenario.Update("Security Updates");

        await scenario.ReportAsync(pilot, RolloutScenario.Update("Feature Packs"), security);

        var rollout = await scenario.Factory.WithDbAsync(db => db.Rollouts.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(security.UpdateId, rollout.UpdateId);
        Assert.Equal(security.UpdateId, Assert.Single((await scenario.ReleasesAsync(pilot)).Releases).UpdateId);
    }

    [Fact]
    public async Task WithoutMatchingStartRule_UpdateStaysQueued()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.ManualStartOnlyAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);

        await scenario.ReportAsync(pilot, RolloutScenario.Update("Security Updates"));

        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.Empty((await scenario.ReleasesAsync(pilot)).Releases);
    }

    [Fact]
    public async Task StartRuleDelay_PostponesInstallationAndTransitionInPilot()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(10, "Security Updates", null, null, StartMode.Automatic, 24, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await scenario.AdministerAsync(admin => admin.SetTransitionAsync(RingEntity.PilotId, 0, 7, 80, 95, false, "admin", CancellationToken.None))).Succeeded);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");

        await scenario.ReportAsync(pilot, update);

        var release = Assert.Single((await scenario.ReleasesAsync(pilot)).Releases);
        Assert.InRange(release.EarliestInstallAt, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));

        // The wait time of the transition starts with the delayed release, even with a zero minimum wait.
        Assert.Equal(0, await scenario.AdvanceAsync());
    }

    [Fact]
    public async Task SuccessfulPilot_ReleasesTest_ThenGateWaitsForApprovalBeforeProduction()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        var update = RolloutScenario.Update("Security Updates");
        foreach (var client in new[] { pilot, test, production })
        {
            await scenario.ReportAsync(client, update);
        }

        // Before the minimum wait time nothing moves, even with a successful result.
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        Assert.Equal(0, await scenario.AdvanceAsync());
        Assert.Empty((await scenario.ReleasesAsync(test)).Releases);

        await scenario.AgeRingAsync(update.UpdateId, RingEntity.PilotId, TimeSpan.FromDays(2));
        Assert.Equal(1, await scenario.AdvanceAsync());
        Assert.Single((await scenario.ReleasesAsync(test)).Releases);

        await scenario.InstallAsync(test, update.UpdateId, InstallResultCode.Succeeded);
        await scenario.AgeRingAsync(update.UpdateId, RingEntity.TestId, TimeSpan.FromDays(3));
        await scenario.AdvanceAsync();
        Assert.Equal(RingRolloutState.AwaitingApproval, await scenario.RingStateAsync(update.UpdateId, RingEntity.ProductionId));
        Assert.Empty((await scenario.ReleasesAsync(production)).Releases);

        var outcome = await scenario.AdministerAsync(admin => admin.ApproveRingAsync(update.UpdateId, 1, RingEntity.ProductionId, "operator", CancellationToken.None));

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Single((await scenario.ReleasesAsync(production)).Releases);
    }

    [Fact]
    public async Task GateSwitchedOff_ReleasesProductionAutomatically()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.SetTransitionAsync(RingEntity.TestId, 0, 10, 80, 95, false, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await scenario.AdministerAsync(admin => admin.SetTransitionAsync(RingEntity.PilotId, 0, 7, 80, 95, false, "admin", CancellationToken.None))).Succeeded);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(production, update);

        // No server in Pilot and Test needs the update, so both transitions pass after a zero minimum wait.
        await scenario.AdvanceAsync();

        Assert.Single((await scenario.ReleasesAsync(production)).Releases);
    }

    [Fact]
    public async Task PoorSuccessRate_RaisesAlarm_ThatAnOperatorCanOverrule()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.ReportAsync(test, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);
        await scenario.AgeRingAsync(update.UpdateId, RingEntity.PilotId, TimeSpan.FromDays(2));

        await scenario.AdvanceAsync();

        Assert.Equal(RingRolloutState.Alarm, await scenario.RingStateAsync(update.UpdateId, RingEntity.TestId));
        Assert.Empty((await scenario.ReleasesAsync(test)).Releases);

        Assert.True((await scenario.AdministerAsync(admin => admin.ApproveRingAsync(update.UpdateId, 1, RingEntity.TestId, "operator", CancellationToken.None))).Succeeded);
        Assert.Single((await scenario.ReleasesAsync(test)).Releases);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Rollout.RingApproved" && e.Details!.Contains("despite alarm"), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SuccessfulRetry_ReplacesEarlierFailureInTheRate()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);

        var statistics = await scenario.WithServiceAsync<RolloutService, RingStatistics>(s => s.GetStatisticsAsync(update.UpdateId, 1, RingEntity.PilotId, CancellationToken.None));

        Assert.Equal(new RingStatistics(1, 1, 1), statistics);
    }

    [Fact]
    public async Task Stop_WithdrawsReleases_AndResumeRestoresThem()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        var before = await scenario.ReleasesAsync(pilot);

        Assert.True((await scenario.AdministerAsync(admin => admin.StopAsync(update.UpdateId, 1, "operator", CancellationToken.None))).Succeeded);
        var stopped = await scenario.ReleasesAsync(pilot);
        Assert.Empty(stopped.Releases);
        Assert.True(stopped.Version > before.Version);

        Assert.True((await scenario.AdministerAsync(admin => admin.StartAsync(update.UpdateId, 1, "operator", CancellationToken.None))).Succeeded);
        Assert.Single((await scenario.ReleasesAsync(pilot)).Releases);
    }

    [Fact]
    public async Task Override_ReleasesQueuedUpdateToEveryRing()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        await scenario.ManualStartOnlyAsync();
        var update = RolloutScenario.Update("Updates");
        await scenario.ReportAsync(production, update);

        var outcome = await scenario.AdministerAsync(admin => admin.OverrideAsync(update.UpdateId, 1, "operator", CancellationToken.None));

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Single((await scenario.ReleasesAsync(production)).Releases);
        Assert.False((await scenario.AdministerAsync(admin => admin.OverrideAsync(update.UpdateId, 1, "operator", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task RingChange_MovesTheReleases()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        Assert.Single((await scenario.ReleasesAsync(client)).Releases);

        await scenario.AssignAsync(client, RingEntity.ProductionId);

        Assert.Empty((await scenario.ReleasesAsync(client)).Releases);
    }

    [Fact]
    public async Task InstalledUpdate_IsNoLongerReleased()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);

        // The next search no longer lists it, but the release stays until the inventory reports it installed.
        await scenario.ReportAsync(client);
        Assert.Single((await scenario.ReleasesAsync(client)).Releases);

        using (await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory("SRV01", new InstalledUpdate { UpdateId = update.UpdateId, Revision = 1, Title = update.Title })))
        {
        }

        Assert.Empty((await scenario.ReleasesAsync(client)).Releases);
    }

    [Fact]
    public async Task UnapprovedClient_GetsNoRelease()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.ReportAsync(client, RolloutScenario.Update("Security Updates"));
        await scenario.Factory.WithDbAsync(db => db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Rejected), TestContext.Current.CancellationToken));

        await scenario.AdvanceAsync();

        Assert.False(await scenario.Factory.WithDbAsync(db => db.Releases.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Worker_RunOnce_AdvancesRollouts()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.SetTransitionAsync(RingEntity.PilotId, 0, 0, 80, 95, false, "admin", CancellationToken.None))).Succeeded);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        await scenario.ReportAsync(test, RolloutScenario.Update("Security Updates"));

        var worker = scenario.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<RolloutWorker>().Single();
        Assert.Equal(1, await worker.RunOnceAsync(CancellationToken.None));

        Assert.Single((await scenario.ReleasesAsync(test)).Releases);
    }
}

public sealed class RolloutAdministrationTests
{
    [Theory]
    [InlineData(RingEntity.PilotId, -1, 7, 80, 95)]
    [InlineData(RingEntity.PilotId, 8, 7, 80, 95)]
    [InlineData(RingEntity.PilotId, 0, 91, 80, 95)]
    [InlineData(RingEntity.PilotId, 2, 7, 101, 95)]
    [InlineData(RingEntity.PilotId, 2, 7, 80, -5)]
    [InlineData(RingEntity.ProductionId, 2, 7, 80, 95)]
    [InlineData(99, 2, 7, 80, 95)]
    public async Task InvalidTransition_IsRefused(int fromRing, int minWait, int maxWait, int feedback, int success)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var outcome = await scenario.AdministerAsync(admin => admin.SetTransitionAsync(fromRing, minWait, maxWait, feedback, success, true, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
        var unchanged = await scenario.Factory.WithDbAsync(db => db.RingTransitions.SingleAsync(t => t.FromRingId == RingEntity.PilotId, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromDays(2), unchanged.MinimumWait);
    }

    [Fact]
    public async Task DefaultRules_HaveManualGateOnlyBeforeProduction()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var transitions = await scenario.Factory.WithDbAsync(db => db.RingTransitions.OrderBy(t => t.FromRingId).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal([false, true], transitions.Select(t => t.ManualGate));
    }

    [Theory]
    [InlineData(-1, null, null, null, (int)StartMode.Automatic, 0)]
    [InlineData(1, null, null, null, (int)StartMode.Automatic, -1)]
    [InlineData(1, null, null, null, (int)StartMode.Automatic, StartRuleEntity.MaxDelayHours + 1)]
    [InlineData(1, null, null, null, (int)StartMode.Manual, 5)]
    [InlineData(1, null, null, null, 7, 0)]
    [InlineData(1, "<script>", null, null, (int)StartMode.Automatic, 0)]
    [InlineData(1, "Security Updates; DROP TABLE", null, null, (int)StartMode.Automatic, 0)]
    [InlineData(1, null, "Very critical", null, (int)StartMode.Automatic, 0)]
    [InlineData(1, null, null, "Cumulative\u0001*", (int)StartMode.Automatic, 0)]
    public async Task InvalidStartRule_IsRefused(int priority, string? classification, string? severity, string? title, int mode, int delay)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var outcome = await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(priority, classification, severity, title, (StartMode)mode, delay, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, await scenario.Factory.WithDbAsync(db => db.StartRules.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task StartRuleChanges_AreAudited_AndUnknownRuleCannotBeDeleted()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        Assert.True((await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(5, " Security Updates ", "Critical", "*Cumulative*", StartMode.Automatic, 48, "alice", CancellationToken.None))).Succeeded);
        Assert.True((await scenario.AdministerAsync(admin => admin.DeleteStartRuleAsync(StartRuleEntity.CatchAllId, "alice", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.DeleteStartRuleAsync(StartRuleEntity.CatchAllId, "alice", CancellationToken.None))).Succeeded);

        var rule = await scenario.Factory.WithDbAsync(db => db.StartRules.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Security Updates", rule.Classification);
        Assert.Equal(TimeSpan.FromHours(48), rule.Delay);
        Assert.Equal(2, await scenario.Factory.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Actor == "alice" && e.Action.StartsWith("StartRule."), TestContext.Current.CancellationToken)));
    }
    [Fact]
    public async Task ActionsOnUnknownOrWrongState_AreRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);

        Assert.False((await scenario.AdministerAsync(admin => admin.StartAsync(Guid.NewGuid(), 1, "operator", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.OverrideAsync(Guid.NewGuid(), 1, "operator", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.StartAsync(update.UpdateId, 1, "operator", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.StopAsync(Guid.NewGuid(), 1, "operator", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.ApproveRingAsync(update.UpdateId, 1, RingEntity.TestId, "operator", CancellationToken.None))).Succeeded);
        Assert.False((await scenario.AdministerAsync(admin => admin.ApproveRingAsync(update.UpdateId, 1, 99, "operator", CancellationToken.None))).Succeeded);
    }
}

public sealed class RolloutPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Reader_SeesRolloutsButCannotStartOne()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.ManualStartOnlyAsync();
        var update = RolloutScenario.Update("Feature Packs");
        await scenario.ReportAsync(pilot, update);
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        using var page = await browser.GetAsync("/updates");
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var post = await browser.PostFormAsync("/updates", new Dictionary<string, string> { ["Action"] = $"start:{update.UpdateId:D}:1" }, pageToRead: "/");

        Assert.Contains("KB5040000", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Start rollout", html, StringComparison.Ordinal);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Operator_StartsRolloutAndApprovesGateThroughPages()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.SetTransitionAsync(RingEntity.PilotId, 0, 7, 80, 95, true, "admin", CancellationToken.None))).Succeeded);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        await scenario.ManualStartOnlyAsync();
        var update = RolloutScenario.Update("Feature Packs");
        await scenario.ReportAsync(test, update);
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using (var started = await browser.PostFormAsync("/updates", new Dictionary<string, string> { ["_handler"] = "updates-bulk", ["Bulk"] = "start", ["Selected"] = $"{update.UpdateId:D}:1" }))
        {
            Assert.Contains("Started the rollout of 1 updates.", await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        await scenario.AdvanceAsync();
        using (var approvals = await browser.GetAsync("/approvals"))
        {
            Assert.Contains("Awaiting approval", await approvals.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using var approved = await browser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "transition", ["Transition"] = $"approve:{update.UpdateId:D}:1:{RingEntity.TestId}" });

        Assert.Contains("The update was released for the ring.", await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Single((await scenario.ReleasesAsync(test)).Releases);
    }

    [Theory]
    [InlineData("start", "not-a-guid:1", "Invalid selection.")]
    [InlineData("start", "00000000-0000-0000-0000-000000000001:-1", "Invalid selection.")]
    [InlineData("delete", "00000000-0000-0000-0000-000000000001:1", "Select at least one update")]
    [InlineData("start", "", "Select at least one update")]
    public async Task Operator_MalformedAction_IsRefused(string action, string selected, string expected)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var response = await browser.PostFormAsync("/updates", new Dictionary<string, string> { ["_handler"] = "updates-bulk", ["Bulk"] = action, ["Selected"] = selected });

        Assert.Contains(expected, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_ChangesTransitionThroughForm()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var response = await browser.PostFormAsync($"/administration/release-rules?dialog=ring&id={RingEntity.TestId}", new Dictionary<string, string>
        {
            ["_handler"] = "release-dialog",
            ["Mode"] = "ring-edit",
            ["Id"] = RingEntity.TestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Name"] = "Test",
            ["MinWait"] = "1",
            ["MaxWait"] = "5",
            ["Feedback"] = "70",
            ["Success"] = "90",
            ["Gate"] = string.Empty,
        });

        Assert.Contains("The ring was saved.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var transition = await scenario.Factory.WithDbAsync(db => db.RingTransitions.SingleAsync(t => t.FromRingId == RingEntity.TestId, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromDays(5), transition.MaximumWait);
        Assert.False(transition.ManualGate);
    }

    [Fact]
    public async Task Admin_NonNumericTransitionValue_IsRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var response = await browser.PostFormAsync($"/administration/release-rules?dialog=ring&id={RingEntity.TestId}", new Dictionary<string, string>
        {
            ["_handler"] = "release-dialog",
            ["Mode"] = "ring-edit",
            ["Id"] = RingEntity.TestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Name"] = "Test",
            ["MinWait"] = "1e3",
            ["MaxWait"] = "5",
            ["Feedback"] = "70",
            ["Success"] = "90",
        });

        Assert.Contains("Wait times must be between", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }


    [Fact]
    public async Task Admin_CreatesStartRuleThroughForm()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var response = await browser.PostFormAsync("/administration/release-rules?dialog=rule-new", new Dictionary<string, string>
        {
            ["_handler"] = "release-dialog",
            ["Mode"] = "rule-create",
            ["Priority"] = "10",
            ["Classification"] = "Security Updates",
            ["StartMode"] = "automatic",
            ["Delay"] = "12",
        });

        Assert.Contains("The start rule was created.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var rule = await scenario.Factory.WithDbAsync(db => db.StartRules.SingleAsync(r => r.Priority == 10, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromHours(12), rule.Delay);
    }

    [Theory]
    [InlineData("rule-create", "10", "sometimes", "0")]
    [InlineData("rule-create", "ten", "automatic", "0")]
    [InlineData("rule-create", "10", "automatic", "-3")]
    [InlineData("rule-delete", "10", "automatic", "0")]
    public async Task Admin_MalformedStartRuleForm_IsRefused(string action, string priority, string mode, string delay)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var response = await browser.PostFormAsync("/administration/release-rules?dialog=rule-new", new Dictionary<string, string>
        {
            ["_handler"] = "release-dialog",
            ["Mode"] = action,
            ["Priority"] = priority,
            ["StartMode"] = mode,
            ["Delay"] = delay,
        });

        Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(1, await scenario.Factory.WithDbAsync(db => db.StartRules.CountAsync(TestContext.Current.CancellationToken)));
    }
    [Fact]
    public async Task ReleaseRules_AreOnlyForAdministrators()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var response = await browser.GetAsync("/administration/release-rules");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}

/// <summary>A central server with approved clients in chosen rings, driven through the agent API.</summary>
internal sealed class RolloutScenario : IAsyncDisposable
{
    private RolloutScenario(ServerFactory factory, HttpClient http)
    {
        Factory = factory;
        Http = http;
    }

    public ServerFactory Factory { get; }

    public HttpClient Http { get; }

    public static Task<RolloutScenario> CreateAsync()
    {
        var factory = new ServerFactory();
        var http = factory.CreateClient();
        return Task.FromResult(new RolloutScenario(factory, http));
    }

    public static AvailableUpdate Update(string classification) => UpdateReportTests.Available() with
    {
        KbArticle = "5040000",
        Classification = classification,
    };

    public async Task<RegisterResponse> AddClientAsync(string host, int ring)
    {
        var credentials = await RegisterTests.RegisterAsync(Http);
        await Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved).SetProperty(c => c.HostName, host), TestContext.Current.CancellationToken));
        await AssignAsync(credentials, ring);
        return credentials;
    }

    public async Task AssignAsync(RegisterResponse client, int ring)
    {
        var outcome = await WithServiceAsync<AssignmentAdministration, Outcome>(admin => admin.SetClientAssignmentAsync(client.ClientId, ring, [], null, "admin", CancellationToken.None));
        Assert.True(outcome.Succeeded, outcome.Error);
    }

    public async Task ReportAsync(RegisterResponse client, params AvailableUpdate[] updates)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentUpdateReport, UriKind.Relative)) { Content = JsonContent.Create(UpdateReportTests.SuccessfulReport(updates)) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public async Task InstallAsync(RegisterResponse client, Guid updateId, InstallResultCode code)
    {
        var report = new InstallReport
        {
            RunId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow,
            Results = [new InstallResult { UpdateId = updateId, Revision = 1, ResultCode = code, HResult = code == InstallResultCode.Failed ? unchecked((int)0x80070005) : 0, RebootRequired = false }],
            RebootRequired = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentInstallReport, UriKind.Relative)) { Content = JsonContent.Create(report) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public async Task<ReleaseList> ReleasesAsync(RegisterResponse client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentReleases, UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReleaseList>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Moves the release of a ring into the past instead of waiting.</summary>
    public Task AgeRingAsync(Guid updateId, int ring, TimeSpan age) =>
        Factory.WithDbAsync(db => db.RolloutRings.Where(r => r.UpdateId == updateId && r.RingId == ring)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleasedAtUtc, DateTime.UtcNow - age - TimeSpan.FromMinutes(1)), TestContext.Current.CancellationToken));

    public Task<RingRolloutState> RingStateAsync(Guid updateId, int ring) =>
        Factory.WithDbAsync(db => db.RolloutRings.Where(r => r.UpdateId == updateId && r.RingId == ring).Select(r => r.State).SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>Removes the default start rule, so every update stays queued until it is started.</summary>
    public Task ManualStartOnlyAsync() =>
        Factory.WithDbAsync(db => db.StartRules.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

    public Task<int> AdvanceAsync() => WithServiceAsync<RolloutService, int>(s => s.AdvanceAsync(CancellationToken.None));

    public Task<Outcome> AdministerAsync(Func<RolloutAdministration, Task<Outcome>> action) => WithServiceAsync(action);

    public async Task<TResult> WithServiceAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        Factory.StartServer();
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Factory.DisposeAsync();
    }
}
