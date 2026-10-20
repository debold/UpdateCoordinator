using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class RingAdministrationTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task NewRing_GoesBeforeTheFinalRing_WhichStaysTheFallback()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SRV01", RingEntity.ProductionId);

        var outcome = await WithRingsAsync(scenario, r => r.CreateAsync("Early adopters", "admin", CancellationToken.None));

        Assert.True(outcome.Succeeded, outcome.Error);
        var rings = await scenario.Factory.WithDbAsync(db => db.Rings.OrderBy(r => r.Order).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["Pilot", "Test", "Early adopters", "Production"], rings.Select(r => r.Name));
        Assert.True(rings[^1].IsDefault);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.RingTransitions.AnyAsync(t => t.FromRingId == rings[2].Id, TestContext.Current.CancellationToken)));
        Assert.Equal(RingEntity.ProductionId, await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == server.ClientId).Select(c => c.RingId).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<script>")]
    [InlineData("Pilot")]
    [InlineData("a name that is far too long for a ring")]
    public async Task InvalidOrDuplicateNames_AreRefused(string name)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        Assert.False((await WithRingsAsync(scenario, r => r.CreateAsync(name, "admin", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task RingsWithServersOrRules_AndTheFinalRing_CannotBeDeleted()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);

        Assert.Contains("still has servers", (await WithRingsAsync(scenario, r => r.DeleteAsync(RingEntity.PilotId, "admin", CancellationToken.None))).Error, StringComparison.Ordinal);
        Assert.Contains("final ring", (await WithRingsAsync(scenario, r => r.DeleteAsync(RingEntity.ProductionId, "admin", CancellationToken.None))).Error, StringComparison.Ordinal);

        await scenario.Factory.WithDbAsync(async db =>
        {
            db.AssignmentRules.Add(new AssignmentRuleEntity { Priority = 1, Target = AssignmentTarget.Ring, TargetId = RingEntity.TestId, Domain = "lab.local" });
            return await db.SaveChangesAsync();
        });
        Assert.Contains("Assignment rules", (await WithRingsAsync(scenario, r => r.DeleteAsync(RingEntity.TestId, "admin", CancellationToken.None))).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyRing_IsDeleted_AndOrdersStayContiguous()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        var outcome = await WithRingsAsync(scenario, r => r.DeleteAsync(RingEntity.TestId, "admin", CancellationToken.None));

        Assert.True(outcome.Succeeded, outcome.Error);
        var rings = await scenario.Factory.WithDbAsync(db => db.Rings.OrderBy(r => r.Order).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([1, 2], rings.Select(r => r.Order));
        Assert.Equal(["Pilot", "Production"], rings.Select(r => r.Name));
    }

    [Fact]
    public async Task StartRuleWithRings_ReleasesTheUpdateInThoseRingsAtOnce()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        await scenario.AdministerAsync(a => a.CreateStartRuleAsync(1, "Definition Updates", null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, [RingEntity.ProductionId]));

        await scenario.ReportAsync(production, RolloutScenario.Update("Definition Updates"));

        var states = await scenario.Factory.WithDbAsync(db => db.RolloutRings.OrderBy(r => r.RingId).Select(r => r.State).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([RingRolloutState.Released, RingRolloutState.Waiting, RingRolloutState.Released], states);
        Assert.Single((await scenario.ReleasesAsync(production)).Releases);
    }

    [Fact]
    public async Task ReleaseRulePage_IsPrefilledFromAnUpdate_AndSavesStartRings()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var dialog = await (await browser.GetAsync("/administration/release-rules?dialog=rule-new&classification=Definition%20Updates&severity=Critical&title=Security%20Intelligence")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<option value=\"Definition Updates\" selected", dialog, StringComparison.Ordinal);
        Assert.Contains("value=\"Security Intelligence\"", dialog, StringComparison.Ordinal);

        using (var saved = await browser.PostFormAsync("/administration/release-rules?dialog=rule-new", new Dictionary<string, string>
        {
            ["_handler"] = "release-dialog", ["Mode"] = "rule-create", ["Priority"] = "5", ["Classification"] = "Definition Updates",
            ["StartMode"] = "automatic", ["Delay"] = "0", ["StartRingsSelected"] = RingEntity.TestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }))
        {
            Assert.Contains("The start rule was created.", await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal("2", await scenario.Factory.WithDbAsync(db => db.StartRules.Where(r => r.Priority == 5).Select(r => r.StartRingIds).SingleAsync(TestContext.Current.CancellationToken)));
    }

    private static async Task<Outcome> WithRingsAsync(RolloutScenario scenario, Func<RingAdministration, Task<Outcome>> action)
    {
        await using var scope = scenario.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<RingAdministration>());
    }
}
