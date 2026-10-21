using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>Expected release of a ring that has not got an update yet.</summary>
/// <param name="EarliestUtc">After the minimum waits, when every ring before reports enough results at once.</param>
/// <param name="LatestUtc">After the maximum waits, when a good success rate is enough without full feedback.</param>
/// <param name="ManualApproval">A manual gate lies on the way: an operator must approve at least one transition.</param>
internal readonly record struct RingForecast(DateTime EarliestUtc, DateTime LatestUtc, bool ManualApproval);

/// <summary>
/// Expected release times of the rings an update has not reached yet, from the release of the last reached ring and the
/// wait times of the transitions (<see cref="TransitionRule"/>). An estimate only: low feedback, alarms and gates change it.
/// </summary>
internal static class RolloutForecast
{
    /// <param name="rings">All rings in order.</param>
    /// <param name="transitions">Transition rules by the ring they leave.</param>
    /// <param name="states">Rollout state of the update per ring; empty if the update has no rollout.</param>
    /// <returns>A forecast for every waiting ring that can be estimated; rings behind an alarm or a stopped rollout get none.</returns>
    public static IReadOnlyDictionary<int, RingForecast> Estimate(
        IReadOnlyList<RingEntity> rings,
        IReadOnlyDictionary<int, RingTransitionEntity> transitions,
        IReadOnlyDictionary<int, RolloutRingEntity> states)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(states);
        var result = new Dictionary<int, RingForecast>();
        (DateTime Earliest, DateTime Latest, bool Manual)? previous = null;
        for (var i = 0; i < rings.Count; i++)
        {
            var state = states.GetValueOrDefault(rings[i].Id);
            if (state is null)
            {
                previous = null;
                continue;
            }

            if (state.State == RingRolloutState.Released && state.ReleasedAtUtc is { } released)
            {
                previous = (released, released, false);
                continue;
            }

            // Waiting for an operator: possible from the time the gate opened; the latest time depends on the operator.
            if (state.State == RingRolloutState.AwaitingApproval)
            {
                previous = null;
                continue;
            }

            if (state.State != RingRolloutState.Waiting || previous is not { } before || i == 0
                || !transitions.TryGetValue(rings[i - 1].Id, out var rule))
            {
                previous = null;
                continue;
            }

            var forecast = new RingForecast(before.Earliest + rule.MinimumWait, before.Latest + rule.MaximumWait, before.Manual || rule.ManualGate);
            result[rings[i].Id] = forecast;
            previous = (forecast.EarliestUtc, forecast.LatestUtc, forecast.ManualApproval);
        }

        return result;
    }
}
