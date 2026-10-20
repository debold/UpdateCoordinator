using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class StartRuleEvaluationTests
{
    private const string Password = "correct horse battery";
    private const string DefenderTitle = "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.421.123.0) - Current Channel (Broad)";

    [Theory]
    [InlineData(DefenderTitle, "Security Intelligence Update for Microsoft Defender Antivirus - KB* (Version *) - Current Channel (Broad)")]
    [InlineData("2026-10 Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5044281)", "* Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB*)")]
    [InlineData("Windows Malicious Software Removal Tool x64", "Windows Malicious Software Removal Tool x64")]
    public void PatternFromTitle_KeepsTheStablePart(string title, string expected)
    {
        Assert.Equal(expected, StartRulePatterns.FromTitle(title));
    }

    [Fact]
    public void PatternFromTitle_MatchesTheNextRelease_AndStaysWithinTheLimit()
    {
        var pattern = StartRulePatterns.FromTitle(DefenderTitle);

        Assert.True(Wildcard.Matches(pattern, "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.421.200.0) - Current Channel (Broad)"));
        Assert.False(Wildcard.Matches(pattern, "2026-10 Cumulative Update for Windows Server"));
        Assert.True(StartRulePatterns.FromTitle(new string('a', 300)).Length <= StartRuleEntity.MaxTitlePatternLength);
    }

    [Fact]
    public async Task NewRule_StartsUpdatesReportedBeforeItExisted()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.ManualStartOnlyAsync();
        var client = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Definition Updates") with { Title = DefenderTitle };
        await scenario.ReportAsync(client, update);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(TestContext.Current.CancellationToken)));

        var outcome = await scenario.AdministerAsync(a => a.CreateStartRuleAsync(100, "Definition Updates", null, StartRulePatterns.FromTitle(DefenderTitle), StartMode.Automatic, 0, "admin", CancellationToken.None));

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == update.UpdateId, TestContext.Current.CancellationToken)));
        Assert.Single((await scenario.ReleasesAsync(client)).Releases);
    }

    [Fact]
    public async Task WaitingUpdates_AreStartedByTheRingLogicToo()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.ManualStartOnlyAsync();
        var client = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        // A rule written straight into the database (e.g. by a restore) is applied by the next ring logic run.
        await scenario.Factory.WithDbAsync(db =>
        {
            db.StartRules.Add(new StartRuleEntity { Priority = 5, Mode = StartMode.Automatic });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        Assert.Equal(1, await scenario.WithServiceAsync<RolloutService, int>(s => s.AutoStartWaitingAsync(CancellationToken.None)));
        Assert.Equal(0, await scenario.WithServiceAsync<RolloutService, int>(s => s.AutoStartWaitingAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Dialog_PreviewsMatches_AndUpdatePageNamesTheRule()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.ManualStartOnlyAsync();
        var client = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Definition Updates") with { Title = DefenderTitle };
        await scenario.ReportAsync(client, update);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var before = await (await admin.GetAsync($"/updates/{update.UpdateId}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("no start rule matches", before, StringComparison.Ordinal);

        var form = new Dictionary<string, string>
        {
            ["_handler"] = "release-dialog", ["Mode"] = "rule-create", ["Preview"] = "yes", ["Classification"] = "Definition Updates", ["Severity"] = "",
            ["Priority"] = "100", ["TitlePattern"] = "*Defender Antivirus*", ["StartMode"] = "automatic", ["Delay"] = "0",
        };
        using (var preview = await admin.PostFormAsync("/administration/release-rules?dialog=rule-new", form))
        {
            var html = await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains($"/updates/{update.UpdateId}/1", html, StringComparison.Ordinal);
            Assert.Contains("waiting: starts when saved", html, StringComparison.Ordinal);
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.StartRules.AnyAsync(TestContext.Current.CancellationToken)));

        form["Preview"] = "";
        using (await admin.PostFormAsync("/administration/release-rules?dialog=rule-new", form))
        {
        }

        var after = await (await admin.GetAsync($"/updates/{update.UpdateId}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("starts automatically", after, StringComparison.Ordinal);
    }
}
