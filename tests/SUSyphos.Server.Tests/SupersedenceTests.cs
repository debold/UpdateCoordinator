using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Tests;

/// <summary>A newer update replaces an older one ring by ring; the older rollout stops once the newer one caught up.</summary>
public sealed class SupersedenceTests
{
    [Fact]
    public async Task NewerUpdate_ReplacesTheOlderOnePerRing_ThenStopsItsRollout()
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        // The old update starts in every ring at once, the new one only in Pilot (catch-all rule).
        Assert.True((await scenario.AdministerAsync(admin => admin.CreateStartRuleAsync(10, null, null, "Old *", StartMode.Automatic, 0, "admin", CancellationToken.None, [RingEntity.TestId, RingEntity.ProductionId]))).Succeeded);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var old = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), Title = "Old cumulative update", KbArticle = "5040001" };
        var newer = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), Title = "New cumulative update", KbArticle = "5040002", SupersededUpdateIds = [old.UpdateId] };

        // The older update started before the newer one was known.
        await scenario.ReportAsync(pilot, old);
        await scenario.ReportAsync(test, old);
        await scenario.ReportAsync(pilot, old, newer);
        await scenario.ReportAsync(test, old, newer);

        // Pilot has the newer update, so the older one is withdrawn there; Test keeps the older one until the newer arrives.
        Assert.Equal([newer.UpdateId], (await scenario.ReleasesAsync(pilot)).Releases.Select(r => r.UpdateId));
        Assert.Equal([old.UpdateId], (await scenario.ReleasesAsync(test)).Releases.Select(r => r.UpdateId));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.UpdateSupersedence.AnyAsync(s => s.UpdateId == newer.UpdateId && s.SupersededUpdateId == old.UpdateId, TestContext.Current.CancellationToken)));
        await scenario.AdvanceAsync();
        Assert.Equal(RolloutState.Active, await RolloutStateAsync(scenario, old.UpdateId));

        // The newer update reaches every ring the older one reached: the older rollout stops.
        Assert.True((await scenario.AdministerAsync(admin => admin.OverrideAsync(newer.UpdateId, 1, "admin", CancellationToken.None))).Succeeded);
        await scenario.AdvanceAsync();

        Assert.Equal(RolloutState.Stopped, await RolloutStateAsync(scenario, old.UpdateId));
        Assert.Equal([newer.UpdateId], (await scenario.ReleasesAsync(test)).Releases.Select(r => r.UpdateId));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Rollout.Superseded" && e.Details!.Contains("KB5040002"), TestContext.Current.CancellationToken)));
        Assert.Equal("KB5040002", (await scenario.Factory.WithDbAsync(db => Supersedence.NewerAsync(db, [old.UpdateId])))[old.UpdateId].Name);
    }

    [Theory]
    [InlineData("2267602", "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.461.144.0) - Current Channel (Broad)", "KB2267602 (Version 1.461.144.0)")]
    [InlineData("5066835", "2026-10 Cumulative Update for Windows Server 2022 (KB5066835)", "KB5066835")]
    [InlineData(null, "Some driver (Version 1.2.3)", "Some driver (Version 1.2.3)")]
    public void ShortName_TellsUpdatesOfOneKbApart(string? kb, string title, string expected) =>
        Assert.Equal(expected, Supersedence.ShortName(kb, title));

    [Fact]
    public async Task UpdateList_HidesSupersededUpdates_UntilAskedFor()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.ManualStartOnlyAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var old = RolloutScenario.Update("Definition Updates") with { UpdateId = Guid.NewGuid(), Title = "Defender - KB2267602 (Version 1.461.115.0)", KbArticle = "2267602" };
        var newer = RolloutScenario.Update("Definition Updates") with { UpdateId = Guid.NewGuid(), Title = "Defender - KB2267602 (Version 1.461.144.0)", KbArticle = "2267602", SupersededUpdateIds = [old.UpdateId] };
        await scenario.ReportAsync(pilot, old, newer);
        await scenario.Factory.CreateUserAsync("reader", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", "correct horse battery"))
        {
        }

        var list = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync("/updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("1.461.115.0", list, StringComparison.Ordinal);
        Assert.Contains("superseded (1)</a>", list, StringComparison.Ordinal);

        var all = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync("/updates?superseded=1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("1.461.115.0", all, StringComparison.Ordinal);
        Assert.Contains($"superseded by <a href=\"/updates/{newer.UpdateId}/1\">KB2267602 (Version 1.461.144.0)</a>", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartRules_DoNotStartAnUpdateANewerOneReplaces()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var old = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), Title = "Old" };
        var newer = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), Title = "New", SupersededUpdateIds = [old.UpdateId] };

        await scenario.ReportAsync(pilot, old, newer);

        Assert.True(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == newer.UpdateId, TestContext.Current.CancellationToken)));
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == old.UpdateId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ServerWithoutTheNewerUpdate_KeepsTheOlderOne()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var other = await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        var old = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), Title = "Old" };
        var newer = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), Title = "New", SupersededUpdateIds = [old.UpdateId] };

        // The older update started before the newer one was known.
        await scenario.ReportAsync(pilot, old);
        await scenario.ReportAsync(other, old);
        await scenario.ReportAsync(pilot, old, newer);

        Assert.Equal([old.UpdateId], (await scenario.ReleasesAsync(other)).Releases.Select(r => r.UpdateId));
        Assert.Equal([newer.UpdateId], (await scenario.ReleasesAsync(pilot)).Releases.Select(r => r.UpdateId));
    }

    private static Task<RolloutState> RolloutStateAsync(RolloutScenario scenario, Guid updateId) =>
        scenario.Factory.WithDbAsync(db => db.Rollouts.Where(r => r.UpdateId == updateId).Select(r => r.State).SingleAsync(TestContext.Current.CancellationToken));
}
