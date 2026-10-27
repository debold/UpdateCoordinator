using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Tests;

/// <summary>One-time maintenance windows on the central server (Marc, 2026-10-27).</summary>
public sealed class OneTimeWindowTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task WindowsReachTheServersOfTheirScope_WithANewReleaseVersion()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        var before = (await scenario.ReleasesAsync(pilot)).Version;
        var start = DateTime.UtcNow.AddDays(1);

        Assert.True((await AddAsync(scenario, WindowScope.Ring, RingEntity.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture), start, start.AddHours(8))).Succeeded);
        Assert.True((await AddAsync(scenario, WindowScope.All, null, start.AddDays(2), start.AddDays(2).AddHours(4))).Succeeded);

        var pilotList = await scenario.ReleasesAsync(pilot);
        var productionList = await scenario.ReleasesAsync(production);
        Assert.True(pilotList.Version > before);
        Assert.Equal(2, pilotList.OneTimeWindows!.Count);
        Assert.Single(productionList.OneTimeWindows!);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "OneTimeWindow.Added", TestContext.Current.CancellationToken)));

        var id = await scenario.Factory.WithDbAsync(db => db.OneTimeWindows.Where(w => w.RingId != null).Select(w => w.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.True((await scenario.WithServiceAsync<OneTimeWindows, Outcome>(w => w.RemoveAsync(id, "admin", CancellationToken.None))).Succeeded);
        Assert.Single((await scenario.ReleasesAsync(pilot)).OneTimeWindows!);
    }

    [Theory]
    [InlineData(1, 0, "end after it starts")]
    [InlineData(1, 24 * 8, "at most 7 days")]
    [InlineData(-48, 2, "past")]
    public async Task InvalidWindows_AreRefused(int startHours, int lengthHours, string message)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var start = DateTime.UtcNow.AddHours(startHours);

        var outcome = await AddAsync(scenario, WindowScope.All, null, start, start.AddHours(lengthHours));

        Assert.Contains(message, outcome.Error, StringComparison.Ordinal);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.OneTimeWindows.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task UnknownTarget_IsRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var start = DateTime.UtcNow.AddDays(1);

        Assert.Contains("Choose all servers", (await AddAsync(scenario, WindowScope.Group, "999", start, start.AddHours(2))).Error, StringComparison.Ordinal);
        Assert.Contains("Choose all servers", (await AddAsync(scenario, WindowScope.Server, "'; drop", start, start.AddHours(2))).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_AddsAWindowForAServer_AndListsIt()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SQL01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var day = SUSyphos.Server.Components.DisplayTime.ToLocal(DateTime.UtcNow).Date.AddDays(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using (var added = await browser.PostFormAndFollowAsync("/administration/maintenance", new Dictionary<string, string>
        {
            ["_handler"] = "one-time", ["OneTimeAction"] = "add", ["OneTimeScope"] = $"Server:{server.ClientId}",
            ["OneTimeStartDate"] = day, ["OneTimeStartTime"] = "06:00", ["OneTimeEndDate"] = day, ["OneTimeEndTime"] = "14:00", ["OneTimeReason"] = "patch weekend",
        }))
        {
            var html = await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("patch weekend", html, StringComparison.Ordinal);
            Assert.Contains("SQL01", html, StringComparison.Ordinal);
        }

        Assert.Single((await scenario.ReleasesAsync(server)).OneTimeWindows!);
    }

    private static Task<Outcome> AddAsync(RolloutScenario scenario, WindowScope scope, string? target, DateTime start, DateTime end) =>
        scenario.WithServiceAsync<OneTimeWindows, Outcome>(w => w.AddAsync(scope, target, start, end, null, "admin", CancellationToken.None));
}

/// <summary>One-time maintenance windows as the agent sees them: one window per local day, joined with the weekly ones.</summary>
public sealed class OneTimeWindowContractTests
{
    [Fact]
    public void WindowOverSeveralDays_BecomesOneEntryPerLocalDay_ThatJoinUp()
    {
        // Saturday 2026-11-07 06:00 to Monday 2026-11-09 02:00 UTC, seen in UTC.
        var window = new OneTimeWindow(new DateTimeOffset(2026, 11, 7, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero));

        var days = window.ToLocalDays(TimeZoneInfo.Utc).ToList();

        Assert.Equal([new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9)], days.Select(d => d.On!.Value));
        Assert.Equal((TimeSpan.FromHours(6), TimeSpan.FromDays(1)), (days[0].Start, days[0].End));
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromHours(2)), (days[2].Start, days[2].End));

        // The days join up into one window; outside it, on other dates, nothing is open.
        var next = MaintenanceSchedule.NextWindow(days, new DateTime(2026, 11, 6, 12, 0, 0));
        Assert.Equal((new DateTime(2026, 11, 7, 6, 0, 0), new DateTime(2026, 11, 9, 2, 0, 0)), next);
        Assert.False(MaintenanceSchedule.IsOpen(days, new DateTime(2026, 11, 14, 8, 0, 0)));
    }

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(2, 2, false)]
    [InlineData(3, 1, false)]
    [InlineData(0, 24 * 8, false)]
    public void OnlyWindowsThatEndAfterTheirStart_AndLastAtMostAWeek_AreValid(int startHour, int endHour, bool valid)
    {
        var day = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(valid, new OneTimeWindow(day.AddHours(startHour), day.AddHours(endHour)).IsValid);
    }
}
