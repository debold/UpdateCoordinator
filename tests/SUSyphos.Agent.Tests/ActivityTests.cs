using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Agent.Status;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class ActivityTrackerTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Begin_ReportsPhaseAndUpdates_AndDisposeEndsIt()
    {
        var tracker = new ActivityTracker(_time);
        var updateId = Guid.NewGuid();

        using (tracker.Begin(AgentPhase.Downloading, [updateId]))
        {
            var current = Assert.IsType<AgentActivity>(tracker.Current);
            Assert.Equal(AgentPhase.Downloading, current.Phase);
            Assert.Equal([updateId], current.UpdateIds);
            Assert.Equal(_time.GetUtcNow(), current.StartedAt);
            Assert.Null(current.Percent);
        }

        Assert.Null(tracker.Current);
    }

    [Fact]
    public void Progress_ChangesPhaseAndPercent_ClampsIt_AndCountsOnlyChanges()
    {
        var tracker = new ActivityTracker(_time);
        using var scope = tracker.Begin(AgentPhase.Downloading);
        var version = tracker.Version;

        tracker.Progress(AgentPhase.Downloading, 150);
        Assert.Equal(100, tracker.Current?.Percent);
        tracker.Progress(AgentPhase.Downloading, 100);
        Assert.Equal(version + 1, tracker.Version);

        tracker.Progress(AgentPhase.Installing, -5);
        Assert.Equal(AgentPhase.Installing, tracker.Current?.Phase);
        Assert.Equal(0, tracker.Current?.Percent);
    }

    [Fact]
    public void Progress_WhileIdle_IsIgnored()
    {
        var tracker = new ActivityTracker(_time);

        tracker.Progress(AgentPhase.Installing, 50);

        Assert.Null(tracker.Current);
        Assert.Equal(0, tracker.Version);
    }

    [Fact]
    public void EndingAReplacedStep_KeepsTheNewOne_AndDoubleDisposeIsHarmless()
    {
        var tracker = new ActivityTracker(_time);
        var first = tracker.Begin(AgentPhase.Searching);
        using var second = tracker.Begin(AgentPhase.Inventory);

        first.Dispose();
        first.Dispose();

        Assert.Equal(AgentPhase.Inventory, tracker.Current?.Phase);
    }

    [Fact]
    public void Begin_TakesAtMostTheAllowedNumberOfUpdates()
    {
        var tracker = new ActivityTracker(_time);

        using var scope = tracker.Begin(AgentPhase.Installing, [.. Enumerable.Range(0, AgentActivity.MaxUpdates + 5).Select(_ => Guid.NewGuid())]);

        Assert.Equal(AgentActivity.MaxUpdates, tracker.Current?.UpdateIds.Count);
    }
}

public sealed class ActivityPulseTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeCentralServerClient _client = new();
    private readonly InMemoryIdentityStore _store = new();

    [Fact]
    public async Task Idle_SendsNothing()
    {
        _store.Save(_client.IssuedIdentity);
        var pulse = Create(new ActivityTracker(_time));

        Assert.False(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_client.ActivityReports);
    }

    [Fact]
    public async Task RunningStep_IsSentOnChange_RepeatedAfterAWhile_AndItsEndOnce()
    {
        _store.Save(_client.IssuedIdentity);
        var tracker = new ActivityTracker(_time);
        var pulse = Create(tracker);
        var step = tracker.Begin(AgentPhase.Downloading);

        Assert.True(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));
        Assert.False(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));

        tracker.Progress(AgentPhase.Downloading, 40);
        Assert.True(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(40, _client.ActivityReports[^1].Activity?.Percent);

        _time.Advance(ActivityPulse.RepeatAfter);
        Assert.True(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));

        step.Dispose();
        Assert.True(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));
        Assert.Null(_client.ActivityReports[^1].Activity);
        Assert.False(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, _client.ActivityReports.Count);
    }

    [Fact]
    public async Task WithoutIdentity_SendsNothing()
    {
        var tracker = new ActivityTracker(_time);
        var pulse = Create(tracker);
        using var step = tracker.Begin(AgentPhase.Inventory);

        Assert.False(await pulse.SendIfDueAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_client.ActivityReports);
    }

    private ActivityPulse Create(ActivityTracker tracker) => new(
        tracker,
        _client,
        _store,
        Options.Create(new CentralServerOptions { BaseAddress = new Uri("https://central.example.local/") }),
        _time,
        NullLogger<ActivityPulse>.Instance);
}
