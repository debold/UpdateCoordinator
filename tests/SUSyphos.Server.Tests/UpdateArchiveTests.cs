using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class UpdateArchiveTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task UnneededUpdates_AreArchived_NeededOnesStay_AndReturnWhenNeededAgain()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var needed = RolloutScenario.Update("Security Updates");
        var stale = needed with { UpdateId = Guid.NewGuid(), KbArticle = "5000001", Title = "Old update" };

        // Without the default start rule nothing is released, so only the missing reports decide.
        await scenario.AdministerAsync(a => a.DeleteStartRuleAsync(StartRuleEntity.CatchAllId, "admin", CancellationToken.None));
        await scenario.ReportAsync(server, needed, stale);
        await scenario.ReportAsync(server, needed);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(91));

        var (archived, deleted) = await RunAsync(scenario, time);

        Assert.Equal(1, archived);
        Assert.Equal(0, deleted);
        Assert.NotNull(await ArchivedAtAsync(scenario, stale.UpdateId));
        Assert.Null(await ArchivedAtAsync(scenario, needed.UpdateId));

        await scenario.ReportAsync(server, needed, stale);
        Assert.Null(await ArchivedAtAsync(scenario, stale.UpdateId));
    }

    [Fact]
    public async Task UpdateWithOpenRelease_IsNotArchived()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(server, update);
        await scenario.AdministerAsync(a => a.StartAsync(update.UpdateId, 1, "operator", CancellationToken.None));
        await scenario.Factory.WithDbAsync(db => db.ClientAvailableUpdates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        var (archived, _) = await RunAsync(scenario, new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(200)));

        Assert.Equal(0, archived);
    }

    [Fact]
    public async Task ArchivedUpdates_AreDeletedWithTheirHistory_AfterTheRetentionTime()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(server, update);
        await scenario.AdministerAsync(a => a.StartAsync(update.UpdateId, 1, "operator", CancellationToken.None));
        await scenario.InstallAsync(server, update.UpdateId, InstallResultCode.Succeeded);
        await scenario.Factory.WithDbAsync(db => db.ClientAvailableUpdates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, (await RunAsync(scenario, new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(91)))).Archived);
        Assert.Equal(1, (await RunAsync(scenario, new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(91 + 366)))).Deleted);

        Assert.False(await scenario.Factory.WithDbAsync(db => db.Updates.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Installations.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task UpdateList_HidesArchivedUpdates_UntilAskedFor()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(server, update);
        await scenario.Factory.WithDbAsync(db => db.Updates.ExecuteUpdateAsync(s => s.SetProperty(u => u.ArchivedAtUtc, DateTime.UtcNow), TestContext.Current.CancellationToken));
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        var current = await (await browser.GetAsync("/updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var archived = await (await browser.GetAsync("/updates?view=archived")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("KB5040000", current, StringComparison.Ordinal);
        Assert.Matches("Archived</span>\\s*<span class=\"count\">1</span>", current);
        Assert.Contains("KB5040000", archived, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:00", "365.00:00:00", false)]
    [InlineData("90.00:00:00", "1.00:00:00", false)]
    [InlineData("90.00:00:00", "365.00:00:00", true)]
    public void Options_AreValidated(string archiveAfter, string deleteAfter, bool valid)
    {
        var options = new UpdateArchiveOptions { ArchiveAfter = TimeSpan.Parse(archiveAfter, System.Globalization.CultureInfo.InvariantCulture), DeleteAfter = TimeSpan.Parse(deleteAfter, System.Globalization.CultureInfo.InvariantCulture) };

        Assert.Equal(valid, System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new(options), [], validateAllProperties: true));
    }

    private static async Task<(int Archived, int Deleted)> RunAsync(RolloutScenario scenario, TimeProvider time)
    {
        await using var scope = scenario.Factory.Services.CreateAsyncScope();
        return await new UpdateArchive(scope.ServiceProvider.GetRequiredService<ServerDbContext>(), Options.Create(new UpdateArchiveOptions()), time, NullLogger<UpdateArchive>.Instance)
            .RunAsync(CancellationToken.None);
    }

    private static Task<DateTime?> ArchivedAtAsync(RolloutScenario scenario, Guid updateId) =>
        scenario.Factory.WithDbAsync(db => db.Updates.Where(u => u.UpdateId == updateId).Select(u => u.ArchivedAtUtc).SingleAsync(TestContext.Current.CancellationToken));
}
