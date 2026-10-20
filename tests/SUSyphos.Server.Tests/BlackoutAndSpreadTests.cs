using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class BlackoutPeriodTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task ActiveBlackout_PausesAgents_WithoutTouchingThePauseSwitch()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        Assert.False((await HeartbeatAsync(scenario, client)).InstallationsPaused);

        var now = DateTime.UtcNow;
        Assert.True((await scenario.WithServiceAsync<BlackoutPeriods, Outcome>(b => b.AddAsync(now.AddHours(-1), now.AddDays(2), "Year-end freeze", "admin", CancellationToken.None))).Succeeded);

        Assert.True((await HeartbeatAsync(scenario, client)).InstallationsPaused);
        Assert.False(await scenario.WithServiceAsync<GlobalPause, bool>(p => p.IsSwitchedOnAsync(CancellationToken.None)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Installation.BlackoutAdded", TestContext.Current.CancellationToken)));

        var period = Assert.Single(await scenario.WithServiceAsync<BlackoutPeriods, IReadOnlyList<BlackoutPeriod>>(b => b.ListAsync(CancellationToken.None)));
        Assert.True((await scenario.WithServiceAsync<BlackoutPeriods, Outcome>(b => b.RemoveAsync(period.Id, "admin", CancellationToken.None))).Succeeded);
        Assert.False((await HeartbeatAsync(scenario, client)).InstallationsPaused);
    }

    [Fact]
    public async Task PlannedBlackout_DoesNotPauseYet()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var now = DateTime.UtcNow;

        await scenario.WithServiceAsync<BlackoutPeriods, Outcome>(b => b.AddAsync(now.AddDays(1), now.AddDays(2), "Later", "admin", CancellationToken.None));

        Assert.False((await HeartbeatAsync(scenario, client)).InstallationsPaused);
    }

    public static TheoryData<int, int, string?> InvalidPeriods() => new()
    {
        { 2, 1, "End before start" },
        { 1, 1, "Same start and end" },
        { -48, -24, "In the past" },
        { 1, 24 * 91, "Too long" },
        { 1, 2, null },
        { 1, 2, "" },
        { 1, 2, "line\nbreak" },
    };

    [Theory]
    [MemberData(nameof(InvalidPeriods))]
    public async Task InvalidPeriods_AreRefused(int startHours, int endHours, string? reason)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var now = DateTime.UtcNow;

        var outcome = await scenario.WithServiceAsync<BlackoutPeriods, Outcome>(b => b.AddAsync(now.AddHours(startHours), now.AddHours(endHours), reason, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task Page_AddsAndRemovesPeriods_AndOverviewShowsTheActiveOne()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        // Entered in the time zone of the central server, in 24-hour format.
        var start = SUSyphos.Server.Components.DisplayTime.ToLocal(DateTime.UtcNow.AddHours(-1));
        var end = SUSyphos.Server.Components.DisplayTime.ToLocal(DateTime.UtcNow.AddDays(1));
        string Date(DateTime value) => value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string Time(DateTime value) => value.ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture);
        using (var added = await admin.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "blackouts", ["BlackoutAction"] = "add", ["BlackoutStartDate"] = Date(start), ["BlackoutStartTime"] = Time(start), ["BlackoutEndDate"] = Date(end), ["BlackoutEndTime"] = Time(end), ["BlackoutReason"] = "Patch freeze" }))
        {
            Assert.Contains("The blackout period was added.", await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var overview = await (await admin.GetAsync("/")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Patch freeze", overview, StringComparison.Ordinal);

        using (var invalid = await admin.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "blackouts", ["BlackoutAction"] = "add", ["BlackoutStartDate"] = "yesterday", ["BlackoutStartTime"] = "25:00", ["BlackoutEndDate"] = Date(end), ["BlackoutEndTime"] = Time(end), ["BlackoutReason"] = "x" }))
        {
            Assert.Contains("Enter start and end", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var id = Assert.Single(await scenario.WithServiceAsync<BlackoutPeriods, IReadOnlyList<BlackoutPeriod>>(b => b.ListAsync(CancellationToken.None))).Id;
        using (var removed = await admin.PostFormAsync("/administration/maintenance", new Dictionary<string, string> { ["_handler"] = "blackouts", ["BlackoutAction"] = $"remove:{id}" }))
        {
            Assert.Contains("The blackout period was removed.", await removed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    private static async Task<HeartbeatResponse> HeartbeatAsync(RolloutScenario scenario, RegisterResponse client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative)) { Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.2.3" }) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!;
    }
}

public sealed class RingSpreadTests
{
    [Fact]
    public void Offset_IsFixedPerServerAndUpdate_AndWithinTheSpread()
    {
        var client = Guid.NewGuid();
        var update = Guid.NewGuid();

        var first = ReleasePlanner.SpreadOffset(client, update, 24);

        Assert.Equal(first, ReleasePlanner.SpreadOffset(client, update, 24));
        Assert.InRange(first, TimeSpan.Zero, TimeSpan.FromHours(24));
        Assert.Equal(TimeSpan.Zero, ReleasePlanner.SpreadOffset(client, update, 0));
        var offsets = Enumerable.Range(0, 200).Select(_ => ReleasePlanner.SpreadOffset(Guid.NewGuid(), update, 24)).ToList();
        Assert.True(offsets.Distinct().Count() > 100, "Offsets should spread across servers.");
        Assert.All(offsets, o => Assert.InRange(o, TimeSpan.Zero, TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task Release_InASpreadRing_StartsWithinTheSpread()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.WithServiceAsync<RingAdministration, Outcome>(r => r.SetSpreadAsync(RingEntity.PilotId, 24, "admin", CancellationToken.None))).Succeeded);
        var client = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        var before = DateTime.UtcNow;

        await scenario.ReportAsync(client, update);

        var release = Assert.Single((await scenario.ReleasesAsync(client)).Releases);
        // The release time plus the fixed offset of this server for this update.
        var releasedAt = release.EarliestInstallAt.UtcDateTime - ReleasePlanner.SpreadOffset(client.ClientId, update.UpdateId, 24);
        Assert.InRange(releasedAt, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(169)]
    public async Task InvalidSpread_IsRefused(int hours)
    {
        await using var scenario = await RolloutScenario.CreateAsync();

        Assert.False((await scenario.WithServiceAsync<RingAdministration, Outcome>(r => r.SetSpreadAsync(RingEntity.PilotId, hours, "admin", CancellationToken.None))).Succeeded);
    }
}
