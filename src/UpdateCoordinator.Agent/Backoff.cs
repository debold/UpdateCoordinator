namespace UpdateCoordinator.Agent;

/// <summary>
/// Exponential back-off for the main loop while the central server is unreachable.
/// </summary>
internal static class Backoff
{
    /// <summary>Delay after <paramref name="consecutiveFailures"/> failures: interval × 2^(failures-1), capped at <paramref name="max"/>.</summary>
    public static TimeSpan Compute(int consecutiveFailures, TimeSpan interval, TimeSpan max)
    {
        if (consecutiveFailures <= 0)
        {
            return interval;
        }

        // Cap the exponent so the multiplication cannot overflow.
        var factor = Math.Pow(2, Math.Min(consecutiveFailures - 1, 30));
        var ticks = Math.Min(interval.Ticks * factor, max.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }
}
