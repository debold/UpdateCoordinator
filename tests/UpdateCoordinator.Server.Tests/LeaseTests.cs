using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Assignment;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class LeaseRuleTests
{
    [Theory]
    [InlineData(2, 25, 5, 1)]
    [InlineData(40, 25, 5, 5)]
    [InlineData(10, 50, 3, 3)]
    [InlineData(8, 50, 10, 4)]
    [InlineData(100, 1, 10, 1)]
    [InlineData(0, 25, 5, 1)]
    public void Capacity_IsPercentWithMaximumAndAtLeastOne(int members, int percent, int maximum, int expected) =>
        Assert.Equal(expected, LeaseService.Capacity(members, percent, maximum));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(30, 2)]
    public void Threshold_IsReachableInSmallGroups(int members, int expected) =>
        Assert.Equal(expected, LeaseService.Threshold(members));
}

public sealed class LeaseTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Leases_AreLimitedPerGroup_AndFreedOnRelease()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var (first, second, _) = await CoordinatedGroupAsync(scenario, "DC01", "DC02");

        var granted = await AcquireAsync(scenario, first);
        var denied = await AcquireAsync(scenario, second);
        var again = await AcquireAsync(scenario, first);

        Assert.True(granted.Granted);
        Assert.NotNull(granted.ExpiresAt);
        Assert.False(denied.Granted);
        Assert.Contains("taken", denied.Reason, StringComparison.Ordinal);
        Assert.True(again.Granted);

        Assert.Equal(HttpStatusCode.NoContent, await ReleaseAsync(scenario, first, succeeded: true));
        Assert.True((await AcquireAsync(scenario, second)).Granted);
        Assert.Equal(HttpStatusCode.NotFound, await ReleaseAsync(scenario, first, succeeded: true));
    }

    [Fact]
    public async Task FailedLeasesInARow_StopTheGroup_UntilAnOperatorResumes()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var (first, second, groupId) = await CoordinatedGroupAsync(scenario, "DC01", "DC02");

        await AcquireAsync(scenario, first);
        await ReleaseAsync(scenario, first, succeeded: false);
        await AcquireAsync(scenario, second);
        await ReleaseAsync(scenario, second, succeeded: false);

        var stopped = await AcquireAsync(scenario, first);
        Assert.False(stopped.Granted);
        Assert.Contains("stopped", stopped.Reason, StringComparison.Ordinal);

        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using (var page = await browser.GetAsync("/"))
        {
            Assert.Contains("leases in a row failed or expired", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var resumed = await browser.PostFormAsync("/approvals", new Dictionary<string, string> { ["_handler"] = "halted", ["ResumeGroup"] = groupId.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
        {
            Assert.Contains("The group issues leases again.", await resumed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.True((await AcquireAsync(scenario, first)).Granted);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Group.LeasesResumed", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SuccessResetsTheFailureCount()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var (first, second, _) = await CoordinatedGroupAsync(scenario, "DC01", "DC02");

        await AcquireAsync(scenario, first);
        await ReleaseAsync(scenario, first, succeeded: false);
        await AcquireAsync(scenario, second);
        await ReleaseAsync(scenario, second, succeeded: true);
        await AcquireAsync(scenario, first);
        await ReleaseAsync(scenario, first, succeeded: false);

        Assert.True((await AcquireAsync(scenario, second)).Granted);
    }

    [Fact]
    public async Task ExpiredLeases_CountAsFailures()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var (first, _, groupId) = await CoordinatedGroupAsync(scenario, "DC01", "DC02");
        await AcquireAsync(scenario, first);
        await scenario.Factory.WithDbAsync(db => db.Leases.ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken));

        Assert.Equal(1, await scenario.WithServiceAsync<LeaseService, int>(s => s.ExpireAsync(CancellationToken.None)));

        Assert.Equal(1, await scenario.Factory.WithDbAsync(db => db.Groups.Where(g => g.Id == groupId).Select(g => g.ConsecutiveLeaseFailures).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(LeaseState.Expired, await scenario.Factory.WithDbAsync(db => db.Leases.Select(l => l.State).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task UncoordinatedServer_NeedsNoLease_AndIsNotTold()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        Assert.True((await AcquireAsync(scenario, client)).Granted);
        Assert.False((await scenario.ReleasesAsync(client)).RequiresLease);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Leases.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CoordinatedMember_IsToldThroughTheReleaseList()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var (first, _, _) = await CoordinatedGroupAsync(scenario, "DC01", "DC02");

        Assert.True((await scenario.ReleasesAsync(first)).RequiresLease);
    }

    [Fact]
    public async Task MemberOfTwoCoordinatedGroups_NeedsCapacityInBoth()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var (first, second, groupA) = await CoordinatedGroupAsync(scenario, "DC01", "DC02");
        var other = await scenario.AddClientAsync("SQL01", RingEntity.PilotId);
        var groupB = await CreateGroupAsync(scenario, "SQL cluster", coordinated: true);
        await AssignAsync(scenario, other, groupB);
        await AssignAsync(scenario, second, groupA, groupB);

        Assert.True((await AcquireAsync(scenario, other)).Granted);
        Assert.False((await AcquireAsync(scenario, second)).Granted);
        Assert.Empty(await scenario.Factory.WithDbAsync(db => db.Leases.Where(l => l.ClientId == second.ClientId).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.True((await AcquireAsync(scenario, first)).Granted);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(101, 5)]
    [InlineData(25, 0)]
    [InlineData(25, 1001)]
    public async Task InvalidCoordinationSettings_AreRefused(int percent, int maximum)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var groupId = await CreateGroupAsync(scenario, "DCs", coordinated: false);

        var outcome = await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupCoordinationAsync(groupId, true, percent, maximum, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Groups.Select(g => g.Coordinated).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Admin_SetsCoordinationThroughGroupsPage()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var groupId = await CreateGroupAsync(scenario, "DCs", coordinated: false);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using (await browser.PostFormAsync("/administration/groups", new Dictionary<string, string>
        {
            ["Action"] = $"save:{groupId}",
            [$"Coordinated_{groupId}"] = "true",
            [$"Percent_{groupId}"] = "50",
            [$"Max_{groupId}"] = "3",
        }))
        {
        }

        using (var refused = await browser.PostFormAsync("/administration/groups", new Dictionary<string, string> { ["Action"] = $"save:{groupId}", [$"Percent_{groupId}"] = "fifty", [$"Max_{groupId}"] = "3" }))
        {
            Assert.Contains("Share and maximum must be whole numbers.", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var group = await scenario.Factory.WithDbAsync(db => db.Groups.SingleAsync(TestContext.Current.CancellationToken));
        Assert.True(group.Coordinated);
        Assert.Equal(50, group.LeasePercent);
        Assert.Equal(3, group.LeaseMaximum);
    }

    private static async Task<(RegisterResponse First, RegisterResponse Second, int GroupId)> CoordinatedGroupAsync(RolloutScenario scenario, string firstHost, string secondHost)
    {
        var first = await scenario.AddClientAsync(firstHost, RingEntity.PilotId);
        var second = await scenario.AddClientAsync(secondHost, RingEntity.PilotId);
        var groupId = await CreateGroupAsync(scenario, "Domain controllers", coordinated: true);
        await AssignAsync(scenario, first, groupId);
        await AssignAsync(scenario, second, groupId);
        return (first, second, groupId);
    }

    private static async Task<int> CreateGroupAsync(RolloutScenario scenario, string name, bool coordinated)
    {
        Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.CreateGroupAsync(name, null, null, "admin", CancellationToken.None))).Succeeded);
        var groupId = await scenario.Factory.WithDbAsync(db => db.Groups.Where(g => g.Name == name).Select(g => g.Id).SingleAsync(TestContext.Current.CancellationToken));
        if (coordinated)
        {
            Assert.True((await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetGroupCoordinationAsync(groupId, true, 25, 5, "admin", CancellationToken.None))).Succeeded);
        }

        return groupId;
    }

    private static async Task AssignAsync(RolloutScenario scenario, RegisterResponse client, params int[] groups)
    {
        var outcome = await scenario.WithServiceAsync<AssignmentAdministration, Outcome>(a => a.SetClientAssignmentAsync(client.ClientId, RingEntity.PilotId, groups, null, "admin", CancellationToken.None));
        Assert.True(outcome.Succeeded, outcome.Error);
    }

    private static async Task<LeaseResponse> AcquireAsync(RolloutScenario scenario, RegisterResponse client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentLease, UriKind.Relative)) { Content = JsonContent.Create(new { }) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LeaseResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<HttpStatusCode> ReleaseAsync(RolloutScenario scenario, RegisterResponse client, bool succeeded)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentLeaseRelease, UriKind.Relative)) { Content = JsonContent.Create(new LeaseRelease { Succeeded = succeeded }) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
