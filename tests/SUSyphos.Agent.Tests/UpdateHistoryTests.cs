using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Wua;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class UpdateHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ToHistory_DropsInvalidIdentities_CleansValues_AndSortsNewestFirst()
    {
        var id = Guid.NewGuid();
        var history = WireFormat.ToHistory(
        [
            new WuaHistoryRecord(id.ToString(), 2, "Older\u0007 update", Now.AddDays(-2), 1, 2, 0, "SUSyphos.Agent"),
            new WuaHistoryRecord(Guid.NewGuid().ToString(), 1, null, Now, 2, 99, unchecked((int)0x80070643), "Automatic\u0001Updates"),
            new WuaHistoryRecord("not-a-guid", 1, "Broken", Now, 1, 2, 0, null),
            new WuaHistoryRecord(Guid.NewGuid().ToString(), -1, "Negative revision", Now, 1, 2, 0, null),
            new WuaHistoryRecord(Guid.NewGuid().ToString(), 1, "Unknown operation", Now, 3, 2, 0, null),
        ]);

        Assert.Equal(2, history.Count);
        Assert.Equal(UpdateOperation.Uninstallation, history[0].Operation);
        Assert.Equal(InstallResultCode.Failed, history[0].ResultCode);
        Assert.Equal(history[0].UpdateId.ToString(), history[0].Title);
        Assert.Equal("Automatic Updates", history[0].ClientApplicationId);
        Assert.Equal(id, history[1].UpdateId);
        Assert.Equal("Older  update", history[1].Title);
        Assert.Equal(TimeSpan.Zero, history[1].Date.Offset);
    }

    [Fact]
    public void ToHistory_KeepsAtMostTheAllowedNumberOfEntries()
    {
        var raw = Enumerable.Range(0, UpdateHistoryEntry.MaxEntries + 10)
            .Select(i => new WuaHistoryRecord(Guid.NewGuid().ToString(), 1, $"Update {i}", Now.AddMinutes(-i), 1, 2, 0, null));

        var history = WireFormat.ToHistory(raw);

        Assert.Equal(UpdateHistoryEntry.MaxEntries, history.Count);
        Assert.Equal("Update 0", history[0].Title);
    }

    [Fact]
    public async Task Collector_SendsTheHistory_OrNullWhenItCannotBeRead()
    {
        var updates = new FakeInstalledUpdates { History = [new UpdateHistoryEntry { UpdateId = Guid.NewGuid(), Revision = 1, Title = "KB1", Date = Now, Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded }] };
        var probe = new FakeSystemProbe();
        var collector = new InventoryCollector(probe, updates, new RoleDetector(probe, Microsoft.Extensions.Options.Options.Create(new InventoryOptions())), new FakeEnvironment());

        Assert.Single((await collector.CollectAsync(TestContext.Current.CancellationToken)).UpdateHistory!);

        updates.History = null;
        Assert.Null((await collector.CollectAsync(TestContext.Current.CancellationToken)).UpdateHistory);
    }
}

public sealed class SupersededIdsTests
{
    [Fact]
    public void AgentReport_KeepsOnlyValidDistinctForeignIds()
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();

        var ids = WireFormat.Superseded([other.ToString(), "not-a-guid", self.ToString(), other.ToString().ToUpperInvariant(), Guid.Empty.ToString()], self);

        Assert.Equal([other], ids);
        Assert.Null(WireFormat.Superseded([], self));
        Assert.Null(WireFormat.Superseded(null, self));
        Assert.Equal(AvailableUpdate.MaxSupersededUpdates, WireFormat.Superseded([.. Enumerable.Range(0, 600).Select(_ => Guid.NewGuid().ToString())], self)!.Count);
    }
}
