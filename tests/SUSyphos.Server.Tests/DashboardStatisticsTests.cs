using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Tests;

public sealed class DashboardStatisticsTests
{
    private const string Password = "correct horse battery";

    [Theory]
    [InlineData(30, 30, 1)]
    [InlineData(90, 90, 1)]
    [InlineData(365, 53, 7)]
    public void Periods_AreDaysUpTo90_AndWeeksBeyond(int days, int count, int length)
    {
        var today = new DateOnly(2026, 10, 23);

        var buckets = DashboardStatistics.Buckets(today, days);

        Assert.Equal(count, buckets.Count);
        Assert.Equal(today, buckets[^1].To);
        Assert.All(buckets, b => Assert.Equal(length - 1, b.To.DayNumber - b.From.DayNumber));
        Assert.True(buckets.Zip(buckets.Skip(1)).All(p => p.First.To.AddDays(1) == p.Second.From));
    }

    [Theory]
    [InlineData(null, StatisticKind.Compliance)]
    [InlineData("errors", StatisticKind.Errors)]
    [InlineData("'; drop table", StatisticKind.Compliance)]
    public void UnknownKeys_FallBackToCompliance(string? key, StatisticKind expected) => Assert.Equal(expected, DashboardStatistics.ParseKind(key));

    [Theory]
    [InlineData("365", 365)]
    [InlineData("7", 90)]
    [InlineData("abc", 90)]
    public void OnlyOfferedPeriods_AreAccepted(string days, int expected) => Assert.Equal(expected, DashboardStatistics.ParsePeriod(days));

    [Fact]
    public async Task EveryStatistic_IsBuiltFromTheData()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        var update = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() };
        await scenario.ReportAsync(pilot, update, RolloutScenario.Update("Drivers") with { UpdateId = Guid.NewGuid() });
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);
        await scenario.WithServiceAsync<ComplianceHistory, int>(async h => { await h.RecordAsync(CancellationToken.None); return 0; });

        var snapshot = Assert.Single(await scenario.WithServiceAsync<ComplianceHistory, List<ComplianceSnapshotEntity>>(h => h.GetAsync(1, CancellationToken.None)));
        Assert.Equal(1, snapshot.FullyPatched);
        Assert.Equal(2, snapshot.OpenReleased);

        foreach (var (kind, _, _) in DashboardStatistics.Kinds)
        {
            var chart = await scenario.WithServiceAsync<DashboardStatistics, ChartData>(s => s.GetAsync(kind, 30, CancellationToken.None));
            Assert.Equal(kind, chart.Kind);
            Assert.All(chart.Series, s => Assert.Equal(chart.Labels.Count, s.Values.Count));
        }

        var updates = await scenario.WithServiceAsync<DashboardStatistics, ChartData>(s => s.GetAsync(StatisticKind.NewUpdates, 30, CancellationToken.None));
        Assert.Equal(["Security Updates", "Drivers"], updates.Series.Select(s => s.Name));
        Assert.Equal(1, updates.Series[0].Values[^1]);
        Assert.Equal("s6", updates.Series[1].Class);

        var compliance = await scenario.WithServiceAsync<DashboardStatistics, ChartData>(s => s.GetAsync(StatisticKind.Compliance, 30, CancellationToken.None));
        Assert.Equal(50, compliance.Series[1].Values[^1]);

        var errors = await scenario.WithServiceAsync<DashboardStatistics, ChartData>(s => s.GetAsync(StatisticKind.Errors, 30, CancellationToken.None));
        Assert.Equal(ChartForm.Ranking, errors.Form);
        Assert.StartsWith("0x80070005", errors.Labels.Single(), StringComparison.Ordinal);
        Assert.Equal(1, errors.Series[0].Values.Single());
    }

    [Fact]
    public async Task Overview_ShowsTheChosenStatistic_AndTheFullyPatchedTile()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.ReportAsync(pilot, RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid() });
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        var html = await (await browser.GetAsync("/?stat=new-updates&days=30")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Fully patched", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"new-updates\" selected", html, StringComparison.Ordinal);
        Assert.Contains("Security Updates: 1", System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("Show as table", html, StringComparison.Ordinal);
    }
    [Fact]
    public async Task TodaysValue_IsLive_EvenWhenTheStoredRecordIsOlder()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.WithServiceAsync<ComplianceHistory, int>(async h => { await h.RecordAsync(CancellationToken.None); return 0; });
        await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);

        var servers = await scenario.WithServiceAsync<DashboardStatistics, ChartData>(s => s.GetAsync(StatisticKind.Servers, 30, CancellationToken.None));
        var stored = Assert.Single(await scenario.WithServiceAsync<ComplianceHistory, List<ComplianceSnapshotEntity>>(h => h.GetAsync(1, CancellationToken.None)));

        Assert.Equal(1, stored.Servers);
        Assert.Equal(2, servers.Series.Sum(s => s.Values[^1]));
    }
}
