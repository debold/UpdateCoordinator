using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Installation;

/// <summary>
/// Pure decision logic for installations (see docs/05-agent-design.md, section 4), free of I/O so every rule is testable.
/// </summary>
internal static class InstallationPlanner
{
    /// <summary>True if <paramref name="localTime"/> lies in one of the windows (local server time).</summary>
    public static bool IsInWindow(IReadOnlyList<MaintenanceWindow> windows, DateTime localTime)
    {
        var time = localTime.TimeOfDay;
        var today = localTime.DayOfWeek;
        var yesterday = localTime.AddDays(-1).DayOfWeek;

        return windows.Any(window => window.End > window.Start
            ? window.Days.Contains(today) && time >= window.Start && time < window.End
            : (window.Days.Contains(today) && time >= window.Start) || (window.Days.Contains(yesterday) && time < window.End));
    }

    /// <summary>
    /// Releases to install now: released, still missing on the server, not attempted recently, and due.
    /// A batch starts only if at least one release is due; releases due within the pull-forward tolerance join it.
    /// </summary>
    /// <param name="releases">Current release list of the central server.</param>
    /// <param name="paused">Global pause announced by the central server.</param>
    /// <param name="missing">Updates the last search found missing; releases not in it are already installed.</param>
    /// <param name="lastAttempts">When each update was last attempted.</param>
    /// <param name="now">Current time.</param>
    /// <param name="options">Tolerance and retry delay.</param>
    public static IReadOnlyList<Release> PlanBatch(
        ReleaseList releases,
        bool paused,
        IReadOnlySet<(Guid UpdateId, int Revision)> missing,
        IReadOnlyDictionary<(Guid UpdateId, int Revision), DateTimeOffset> lastAttempts,
        DateTimeOffset now,
        InstallationOptions options)
    {
        if (paused)
        {
            return [];
        }

        var candidates = releases.Releases
            .Where(r => missing.Contains((r.UpdateId, r.Revision)))
            .Where(r => !lastAttempts.TryGetValue((r.UpdateId, r.Revision), out var attempted) || now - attempted >= options.RetryDelay)
            .ToList();

        if (!candidates.Any(r => r.EarliestInstallAt <= now))
        {
            // Nothing is due: never install early just because something becomes due soon.
            return [];
        }

        return [.. candidates.Where(r => r.EarliestInstallAt <= now + options.PullForwardTolerance).OrderBy(r => r.EarliestInstallAt)];
    }
}
