using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>Results of the servers of one ring for one update.</summary>
/// <param name="Targeted">Servers of the ring the update was released to.</param>
/// <param name="Reported">Targeted servers that reported an installation result.</param>
/// <param name="Succeeded">Reported servers whose last result was successful.</param>
internal readonly record struct RingStatistics(int Targeted, int Reported, int Succeeded)
{
    public int? FeedbackPercent => Targeted == 0 ? null : Reported * 100 / Targeted;

    public int? SuccessPercent => Reported == 0 ? null : Succeeded * 100 / Reported;
}

internal enum TransitionDecision
{
    Wait,
    Proceed,
    Alarm,
}

/// <summary>
/// Evaluates a ring transition for one update (docs/04-central-server.md, section 3, "Rates"):
/// feedback and success rate are evaluated separately, so offline servers do not distort the success rate.
/// </summary>
internal static class TransitionRule
{
    public static (TransitionDecision Decision, string? Reason) Evaluate(RingTransitionEntity rule, RingStatistics statistics, DateTime releasedAtUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var elapsed = nowUtc - releasedAtUtc;
        if (elapsed < rule.MinimumWait)
        {
            return (TransitionDecision.Wait, null);
        }

        // No server of the ring needs the update: nothing to wait for after the minimum wait time.
        if (statistics.Targeted == 0)
        {
            return (TransitionDecision.Proceed, null);
        }

        var feedbackMet = statistics.Reported * 100 >= statistics.Targeted * rule.MinimumFeedbackPercent;
        var successMet = statistics.Reported > 0 && statistics.Succeeded * 100 >= statistics.Reported * rule.MinimumSuccessPercent;

        if (feedbackMet)
        {
            return successMet
                ? (TransitionDecision.Proceed, null)
                : (TransitionDecision.Alarm, $"Success rate {statistics.SuccessPercent}% is below {rule.MinimumSuccessPercent}%.");
        }

        if (elapsed < rule.MaximumWait)
        {
            return (TransitionDecision.Wait, null);
        }

        // Maximum wait time reached without enough feedback: proceed only on a good success rate of the reports so far.
        if (statistics.Reported == 0)
        {
            return (TransitionDecision.Alarm, "No server reported a result within the maximum wait time.");
        }

        return successMet
            ? (TransitionDecision.Proceed, null)
            : (TransitionDecision.Alarm, $"Success rate {statistics.SuccessPercent}% is below {rule.MinimumSuccessPercent}% at the maximum wait time.");
    }
}
