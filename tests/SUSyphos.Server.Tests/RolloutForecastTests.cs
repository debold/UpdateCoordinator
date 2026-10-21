using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Tests;

public sealed class RolloutForecastTests
{
    private static readonly DateTime Released = new(2026, 10, 20, 22, 49, 0, DateTimeKind.Utc);

    private static readonly List<RingEntity> Rings =
    [
        new() { Id = 1, Order = 1, Name = "Pilot" },
        new() { Id = 2, Order = 2, Name = "Test" },
        new() { Id = 3, Order = 3, Name = "Production" },
    ];

    private static readonly Dictionary<int, RingTransitionEntity> Transitions = new()
    {
        [1] = new() { FromRingId = 1, MinimumWait = TimeSpan.FromDays(2), MaximumWait = TimeSpan.FromDays(7) },
        [2] = new() { FromRingId = 2, MinimumWait = TimeSpan.FromDays(3), MaximumWait = TimeSpan.FromDays(10), ManualGate = true },
    };

    private static RolloutRingEntity State(int ringId, RingRolloutState state, DateTime? released = null) =>
        new() { RingId = ringId, State = state, ReleasedAtUtc = released };

    [Fact]
    public void WaitingRings_AddUpTheWaitTimes_AndCarryTheManualGate()
    {
        var states = new Dictionary<int, RolloutRingEntity>
        {
            [1] = State(1, RingRolloutState.Released, Released),
            [2] = State(2, RingRolloutState.Waiting),
            [3] = State(3, RingRolloutState.Waiting),
        };

        var forecast = RolloutForecast.Estimate(Rings, Transitions, states);

        Assert.False(forecast.ContainsKey(1));
        Assert.Equal(new RingForecast(Released.AddDays(2), Released.AddDays(7), false), forecast[2]);
        Assert.Equal(new RingForecast(Released.AddDays(5), Released.AddDays(17), true), forecast[3]);
    }

    [Theory]
    [InlineData((int)RingRolloutState.Alarm)]
    [InlineData((int)RingRolloutState.AwaitingApproval)]
    public void RingsBehindAnAlarmOrAGate_GetNoForecast(int blocked)
    {
        var states = new Dictionary<int, RolloutRingEntity>
        {
            [1] = State(1, RingRolloutState.Released, Released),
            [2] = State(2, (RingRolloutState)blocked),
            [3] = State(3, RingRolloutState.Waiting),
        };

        var forecast = RolloutForecast.Estimate(Rings, Transitions, states);

        Assert.Empty(forecast);
    }

    [Fact]
    public void WithoutRolloutOrTransition_NothingIsEstimated()
    {
        Assert.Empty(RolloutForecast.Estimate(Rings, Transitions, new Dictionary<int, RolloutRingEntity>()));

        var states = new Dictionary<int, RolloutRingEntity>
        {
            [1] = State(1, RingRolloutState.Released, Released),
            [2] = State(2, RingRolloutState.Waiting),
        };
        Assert.Empty(RolloutForecast.Estimate(Rings, new Dictionary<int, RingTransitionEntity>(), states));
    }
}
