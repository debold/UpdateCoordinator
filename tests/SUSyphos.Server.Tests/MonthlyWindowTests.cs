using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Components;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Tests;

public sealed class MonthlyWindowTests
{
    // November 2026: Tuesdays on the 3rd, 10th, 17th and 24th; Saturdays on the 7th, 14th, 21st and 28th.
    private static readonly MaintenanceWindow SecondTuesday = new() { Days = [DayOfWeek.Tuesday], Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(2), Weeks = [WeekOfMonth.Second] };

    private static readonly MaintenanceWindow SaturdayButLast = new() { Days = [DayOfWeek.Saturday], Start = TimeSpan.Zero, End = TimeSpan.FromDays(1), ExceptWeeks = [WeekOfMonth.Last] };

    [Theory]
    [InlineData(2026, 11, 3, false)]
    [InlineData(2026, 11, 10, true)]
    [InlineData(2026, 11, 17, false)]
    [InlineData(2026, 11, 11, false)]
    public void SecondTuesday_StartsOnlyOnTheSecondTuesday(int year, int month, int day, bool expected)
    {
        Assert.Equal(expected, SecondTuesday.StartsOn(new DateTime(year, month, day)));
    }

    [Fact]
    public void ExceptLast_LeavesOutTheLastSaturday_WhetherItIsTheFourthOrTheFifth()
    {
        Assert.True(SaturdayButLast.StartsOn(new DateTime(2026, 11, 21)));
        Assert.False(SaturdayButLast.StartsOn(new DateTime(2026, 11, 28)));

        // August 2026 has five Saturdays: the 29th is the last, the 22nd (fourth) is not.
        Assert.True(SaturdayButLast.StartsOn(new DateTime(2026, 8, 22)));
        Assert.False(SaturdayButLast.StartsOn(new DateTime(2026, 8, 29)));
    }

    [Fact]
    public void AWindowOverMidnight_BelongsToTheDayItStarts()
    {
        IReadOnlyList<MaintenanceWindow> windows = [SecondTuesday];

        Assert.True(MaintenanceSchedule.IsOpen(windows, new DateTime(2026, 11, 10, 23, 0, 0)));
        Assert.True(MaintenanceSchedule.IsOpen(windows, new DateTime(2026, 11, 11, 1, 30, 0)));
        Assert.False(MaintenanceSchedule.IsOpen(windows, new DateTime(2026, 11, 18, 1, 30, 0)));
        Assert.False(MaintenanceSchedule.IsOpen(windows, new DateTime(2026, 11, 17, 23, 0, 0)));
    }

    [Fact]
    public void NextOpening_FindsAWindowWeeksAhead()
    {
        IReadOnlyList<MaintenanceWindow> windows = [SecondTuesday];

        Assert.Equal(new DateTime(2026, 12, 8, 22, 0, 0), MaintenanceSchedule.NextOpening(windows, new DateTime(2026, 11, 12, 8, 0, 0)));
        Assert.Equal(
            [new DateTime(2026, 12, 8, 22, 0, 0), new DateTime(2027, 1, 12, 22, 0, 0)],
            WindowPickerForm.NextOpenings(windows, new DateTime(2026, 11, 12), 2));
    }

    [Theory]
    [InlineData("Tue 22:00-02:00 weeks 2", "Tue 22:00-02:00 weeks 2")]
    [InlineData("Sat 00:00-24:00 except last", "Sat 00:00-24:00 except last")]
    [InlineData("Mon-Fri 19:00-05:00 except 1,last", "Mon,Tue,Wed,Thu,Fri 19:00-05:00 except 1,last")]
    [InlineData("Sun 01:00-03:00", "Sun 01:00-03:00")]
    public void TextFormat_RoundTripsWeekRules(string text, string formatted)
    {
        Assert.True(MaintenanceWindowFormat.TryParse(text, out var windows, out var error), error);
        Assert.Equal(formatted, MaintenanceWindowFormat.Format(windows));
        Assert.Equal(windows, MaintenanceWindowFormat.FromJson(MaintenanceWindowFormat.ToJson(windows)), new WindowComparer());
    }

    [Theory]
    [InlineData("Tue 22:00-02:00 weeks 5")]
    [InlineData("Tue 22:00-02:00 weeks 2,2")]
    [InlineData("Tue 22:00-02:00 weeks")]
    [InlineData("Tue 22:00-02:00 sometimes 2")]
    [InlineData("Tue 22:00-02:00 except first")]
    public void TextFormat_RejectsInvalidWeekRules(string text)
    {
        Assert.False(MaintenanceWindowFormat.TryParse(text, out _, out _));
    }

    [Fact]
    public void Window_WithBothRules_IsInvalid()
    {
        var window = SecondTuesday with { ExceptWeeks = [WeekOfMonth.Last] };

        Assert.NotEmpty(window.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(window)));
    }

    [Fact]
    public void Picker_ReadsTheWeekRuleOfARow_AndIgnoresUnknownWeeks()
    {
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["W.0.days"] = "2",
            ["W.0.from"] = "22:00",
            ["W.0.to"] = "02:00",
            ["W.0.weekmode"] = "only",
            ["W.0.weeks"] = new(["2", "9", "<script>"]),
            ["W.1.days"] = "6",
            ["W.1.from"] = "00:00",
            ["W.1.to"] = "24:00",
            ["W.1.weekmode"] = "except",
            ["W.1.weeks"] = "last",
            ["W.2.days"] = "0",
            ["W.2.from"] = "01:00",
            ["W.2.to"] = "03:00",
            ["W.2.weekmode"] = "bogus",
            ["W.2.weeks"] = "1",
        });

        var text = WindowPickerForm.Read(form, "W", out _);

        Assert.Contains("Tue 22:00-02:00 weeks 2", text, StringComparison.Ordinal);
        Assert.Contains("Sat 00:00-24:00 except last", text, StringComparison.Ordinal);

        // An unknown mode makes its line invalid instead of silently changing the rule.
        Assert.False(MaintenanceWindowFormat.TryParse(text, out _, out _));
        Assert.Equal("Tue 22:00–02:00 (2nd week of the month)", WindowPickerForm.Describe([SecondTuesday]));
    }

    [Fact]
    public async Task MonthlyWindows_ReachTheAgent_WithTheReleases()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.ReportAsync(client, RolloutScenario.Update("Security Updates"));
        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientAssignmentAsync(client.ClientId, RingEntity.PilotId, [], "Tue 22:00-02:00 weeks 2", "admin", CancellationToken.None))).Succeeded);

        var releases = await scenario.ReleasesAsync(client);

        Assert.Single(releases.Releases);
        Assert.Equal([WeekOfMonth.Second], Assert.Single(releases.MaintenanceWindows!).Weeks);
    }
    private sealed class WindowComparer : IEqualityComparer<MaintenanceWindow>
    {
        public bool Equals(MaintenanceWindow? x, MaintenanceWindow? y) =>
            x is not null && y is not null && x.Days.SequenceEqual(y.Days) && x.Start == y.Start && x.End == y.End
            && (x.Weeks ?? []).SequenceEqual(y.Weeks ?? []) && (x.ExceptWeeks ?? []).SequenceEqual(y.ExceptWeeks ?? []);

        public int GetHashCode(MaintenanceWindow obj) => obj.Start.GetHashCode();
    }
}
