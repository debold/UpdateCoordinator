using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Start rules with a restart condition, and updates that may install outside the maintenance windows.</summary>
public sealed class OutsideWindowsTests
{
    [Fact]
    public async Task RuleForUpdatesWithoutRestart_LetsThemInstallOutsideTheWindows()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(
            10, "Definition Updates", null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, rebootBehavior: RebootBehavior.NeverReboots, outsideWindows: true))).Succeeded);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var definition = RolloutScenario.Update("Definition Updates") with { UpdateId = Guid.NewGuid(), RebootBehavior = RebootBehavior.NeverReboots };
        var restarting = RolloutScenario.Update("Definition Updates") with { UpdateId = Guid.NewGuid(), RebootBehavior = RebootBehavior.CanRequestReboot };

        await scenario.ReportAsync(pilot, definition, restarting);

        var releases = (await scenario.ReleasesAsync(pilot)).Releases;
        Assert.True(Assert.Single(releases, r => r.UpdateId == definition.UpdateId).OutsideWindows);

        // The restart condition keeps the other update away from the rule: it waits for the windows.
        Assert.False(releases.SingleOrDefault(r => r.UpdateId == restarting.UpdateId)?.OutsideWindows ?? false);
        var rollout = await scenario.Factory.WithDbAsync(db => db.Rollouts.SingleAsync(r => r.UpdateId == definition.UpdateId, TestContext.Current.CancellationToken));
        Assert.True(rollout.OutsideWindows);
        Assert.EndsWith("outside maintenance windows", rollout.StartRuleSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThatRestartsAfterAll_IsNotDeliveredOutsideTheWindows()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(
            10, "Definition Updates", null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, rebootBehavior: RebootBehavior.NeverReboots, outsideWindows: true))).Succeeded);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var definition = RolloutScenario.Update("Definition Updates") with { UpdateId = Guid.NewGuid(), RebootBehavior = RebootBehavior.NeverReboots };
        await scenario.ReportAsync(pilot, definition);

        // The catalog changes its mind (e.g. a corrected report): the release goes back into the windows.
        await scenario.Factory.WithDbAsync(db => db.Updates.Where(u => u.UpdateId == definition.UpdateId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RebootBehavior, (int)RebootBehavior.AlwaysRequiresReboot), TestContext.Current.CancellationToken));

        Assert.False(Assert.Single((await scenario.ReleasesAsync(pilot)).Releases).OutsideWindows);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(RebootBehavior.CanRequestReboot, true)]
    [InlineData(RebootBehavior.AlwaysRequiresReboot, true)]
    [InlineData((RebootBehavior)7, false)]
    public async Task OutsideWindows_NeedsTheConditionNoRestart(RebootBehavior? behavior, bool outsideWindows)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var outcome = await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(
            10, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, rebootBehavior: behavior, outsideWindows: outsideWindows));

        Assert.False(outcome.Succeeded);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.StartRules.AnyAsync(r => r.Priority == 10, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void RestartCondition_MatchesOnlyUpdatesWithThatBehaviour()
    {
        var rule = new StartRuleEntity { Id = 1, Priority = 1, RebootBehavior = (int)RebootBehavior.NeverReboots };
        static UpdateEntity Update(int? behavior) => new() { UpdateId = Guid.NewGuid(), Revision = 1, Title = "t", RebootBehavior = behavior };

        Assert.NotNull(RolloutService.FindStartRule([rule], Update((int)RebootBehavior.NeverReboots)));
        Assert.Null(RolloutService.FindStartRule([rule], Update((int)RebootBehavior.CanRequestReboot)));
        Assert.Null(RolloutService.FindStartRule([rule], Update(null)));
    }

    [Fact]
    public async Task DefaultRuleForDefinitionUpdates_IsCreatedOnce_AndStaysDeleted()
    {
        // The test server switches the startup creation off (Rollout:CreateDefinitionRule), so the call here is the first.
        await using var factory = new ServerFactory();
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<RolloutAdministration>();
        Assert.True(await administration.EnsureDefinitionRuleAsync(CancellationToken.None));

        var rule = await factory.WithDbAsync(db => db.StartRules.AsNoTracking().SingleAsync(r => r.Classification == DefinitionUpdates.Classification, TestContext.Current.CancellationToken));
        Assert.True(rule.OutsideWindows);
        Assert.Equal((int)RebootBehavior.NeverReboots, rule.RebootBehavior);
        Assert.Equal(StartMode.Automatic, rule.Mode);
        Assert.NotNull(rule.StartRingIds);
        Assert.NotEqual(1000, rule.Priority);
        Assert.False(await administration.EnsureDefinitionRuleAsync(CancellationToken.None));

        Assert.True((await administration.DeleteStartRuleAsync(rule.Id, "admin", CancellationToken.None)).Succeeded);
        Assert.False(await administration.EnsureDefinitionRuleAsync(CancellationToken.None));
        Assert.False(await factory.WithDbAsync(db => db.StartRules.AnyAsync(r => r.Classification == DefinitionUpdates.Classification, TestContext.Current.CancellationToken)));
    }
}
