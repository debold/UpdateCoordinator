using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Tests;

/// <summary>Holding an update for a ring, a group or a server (Marc, 2026-10-27).</summary>
public sealed class UpdateHoldTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task HoldForAServer_WithholdsItsRelease_AndResumeBringsItBack()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var held = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var other = await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(held, update);
        await scenario.ReportAsync(other, update);

        var outcome = await HoldAsync(scenario, update.UpdateId, HoldTarget.Server, held.ClientId.ToString(), "Exchange crashes");

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Empty((await scenario.ReleasesAsync(held)).Releases);
        Assert.Single((await scenario.ReleasesAsync(other)).Releases);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Update.Held" && a.Details!.Contains("Exchange crashes"), TestContext.Current.CancellationToken)));

        var holdId = await scenario.Factory.WithDbAsync(db => db.UpdateHolds.Select(h => h.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.True((await scenario.WithServiceAsync<UpdateHolds, Outcome>(h => h.ResumeAsync(update.UpdateId, 1, holdId, "operator", CancellationToken.None))).Succeeded);
        Assert.Single((await scenario.ReleasesAsync(held)).Releases);
    }

    [Fact]
    public async Task HoldForAGroup_WithholdsTheReleaseOfItsMembers()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var member = await scenario.AddClientAsync("SQL01", RingEntity.PilotId);
        var outsider = await scenario.AddClientAsync("WEB01", RingEntity.PilotId);
        var groupId = await scenario.Factory.WithDbAsync(async db =>
        {
            var group = new GroupEntity { Name = "SQL cluster" };
            db.Groups.Add(group);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientGroups.Add(new ClientGroupEntity { ClientId = member.ClientId, GroupId = group.Id, Manual = true });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return group.Id;
        });
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(member, update);
        await scenario.ReportAsync(outsider, update);

        Assert.True((await HoldAsync(scenario, update.UpdateId, HoldTarget.Group, groupId.ToString(System.Globalization.CultureInfo.InvariantCulture))).Succeeded);

        Assert.Empty((await scenario.ReleasesAsync(member)).Releases);
        Assert.Single((await scenario.ReleasesAsync(outsider)).Releases);
    }

    [Fact]
    public async Task HeldRing_DoesNotCountAsDone_SoTheNextRingWaits()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.ReportAsync(test, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        await scenario.AgeRingAsync(update.UpdateId, RingEntity.PilotId, TimeSpan.FromDays(2));

        Assert.True((await HoldAsync(scenario, update.UpdateId, HoldTarget.Ring, RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture))).Succeeded);

        Assert.Equal(0, await scenario.AdvanceAsync());
        Assert.Empty((await scenario.ReleasesAsync(test)).Releases);
    }

    [Fact]
    public async Task HoldThatReachedItsEnd_IsDeleted_AndTheReleaseGoesOutAgain()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        Assert.True((await HoldAsync(scenario, update.UpdateId, HoldTarget.Ring, RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture), until: DateTime.UtcNow.AddHours(1))).Succeeded);
        Assert.Empty((await scenario.ReleasesAsync(pilot)).Releases);

        await scenario.Factory.WithDbAsync(db => db.UpdateHolds.ExecuteUpdateAsync(s => s.SetProperty(h => h.UntilUtc, DateTime.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken));
        Assert.Equal(1, await scenario.WithServiceAsync<UpdateHolds, int>(h => h.DeleteEndedAsync(CancellationToken.None)));
        await scenario.AdvanceAsync();

        Assert.Single((await scenario.ReleasesAsync(pilot)).Releases);
    }

    [Theory]
    [InlineData("Ring", "999", null, "Choose the ring")]
    [InlineData("Server", "not-a-guid", null, "Choose the ring")]
    [InlineData("Ring", "1", "line\nbreak", "one line")]
    public async Task InvalidHolds_AreRefused(string target, string targetId, string? reason, string message)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);

        var outcome = await HoldAsync(scenario, update.UpdateId, Enum.Parse<HoldTarget>(target), targetId, reason);

        Assert.Contains(message, outcome.Error, StringComparison.Ordinal);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.UpdateHolds.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SecondHoldForTheSameTarget_AndPastEnd_AreRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        var ring = RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.True((await HoldAsync(scenario, update.UpdateId, HoldTarget.Ring, ring)).Succeeded);
        Assert.Contains("already on hold", (await HoldAsync(scenario, update.UpdateId, HoldTarget.Ring, ring)).Error, StringComparison.Ordinal);
        Assert.Contains("future", (await HoldAsync(scenario, update.UpdateId, HoldTarget.Ring, RingEntity.TestId.ToString(System.Globalization.CultureInfo.InvariantCulture), until: DateTime.UtcNow.AddHours(-1))).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatePage_HoldsThroughTheDialog_ShowsTheBanner_AndReadersCannotHold()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        var path = $"/updates/{update.UpdateId}/1?dialog=hold";

        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using (var reader = new SUSyphos.TestSupport.DashboardClient(scenario.Factory))
        {
            using (await reader.LoginAsync("reader", Password))
            {
            }

            using (await reader.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "hold", ["HoldTarget"] = "Ring", ["HoldRing"] = RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
            {
            }
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.UpdateHolds.AnyAsync(TestContext.Current.CancellationToken)));

        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        Assert.Contains("Hold update", await (await browser.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using (var held = await browser.PostFormAndFollowAsync(path, new Dictionary<string, string> { ["_handler"] = "hold", ["HoldTarget"] = "Ring", ["HoldRing"] = RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["HoldReason"] = "Exchange crashes" }))
        {
            var html = System.Net.WebUtility.HtmlDecode(await held.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Contains("On hold for ring", html, StringComparison.Ordinal);
            Assert.Contains("Exchange crashes", html, StringComparison.Ordinal);
            Assert.Contains("value=\"resume:", html, StringComparison.Ordinal);
        }

        Assert.Empty((await scenario.ReleasesAsync(pilot)).Releases);
    }

    private static Task<Outcome> HoldAsync(RolloutScenario scenario, Guid updateId, HoldTarget target, string targetId, string? reason = null, DateTime? until = null) =>
        scenario.WithServiceAsync<UpdateHolds, Outcome>(h => h.HoldAsync(updateId, 1, target, targetId, reason, until, "operator", CancellationToken.None));
}
