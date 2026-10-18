using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Installation;

internal enum RebootDecision
{
    Reboot,
    NotPending,
    NotAllowedByPolicy,
    LocalVeto,
    Paused,
    OutsideWindow,
    WindowEndsSoon,
    RecentlyRequested,
}

/// <summary>
/// Pure decision logic for reboots (docs/05-agent-design.md, section 4): the agent restarts the server only when a reboot is
/// pending, the central policy allows it, no local veto applies, installations are not paused, a maintenance window is open
/// long enough and no restart was requested recently. Free of I/O so every rule is testable.
/// </summary>
internal static class RebootPlanner
{
    /// <summary>The window must stay open at least this long, so the restart and the boot finish inside it.</summary>
    public static readonly TimeSpan MinimumRemainingWindow = TimeSpan.FromMinutes(30);

    /// <summary>Signed-in users are warned this long before the restart.</summary>
    public static readonly TimeSpan WarningPeriod = TimeSpan.FromMinutes(5);

    /// <summary>No second restart request within this time, so a failed or cancelled restart never loops.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

    public static RebootDecision Decide(
        RebootPolicy policy,
        InstallationOptions options,
        bool paused,
        bool rebootPending,
        IReadOnlyList<MaintenanceWindow> windows,
        DateTime localNow,
        DateTimeOffset? lastRequestAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!rebootPending)
        {
            return RebootDecision.NotPending;
        }

        if (policy != RebootPolicy.InsideMaintenanceWindow)
        {
            return RebootDecision.NotAllowedByPolicy;
        }

        if (!options.AllowReboot || !options.Enabled)
        {
            return RebootDecision.LocalVeto;
        }

        if (paused)
        {
            return RebootDecision.Paused;
        }

        if (RemainingWindow(windows, localNow) is not { } remaining)
        {
            return RebootDecision.OutsideWindow;
        }

        if (remaining < MinimumRemainingWindow)
        {
            return RebootDecision.WindowEndsSoon;
        }

        return lastRequestAt is { } last && now - last < Cooldown ? RebootDecision.RecentlyRequested : RebootDecision.Reboot;
    }

    /// <summary>
    /// Time until the open maintenance window closes, following adjoining windows (e.g. Saturday and Sunday all day);
    /// null outside a window.
    /// </summary>
    public static TimeSpan? RemainingWindow(IReadOnlyList<MaintenanceWindow> windows, DateTime localNow)
    {
        if (!InstallationPlanner.IsInWindow(windows, localNow))
        {
            return null;
        }

        // Walk forward in minute steps up to a week; windows end on full minutes, so this finds the end exactly.
        var probe = localNow;
        var limit = localNow.AddDays(7);
        while (probe < limit && InstallationPlanner.IsInWindow(windows, probe))
        {
            probe = probe.AddMinutes(1);
        }

        var end = new DateTime(probe.Year, probe.Month, probe.Day, probe.Hour, probe.Minute, 0, probe.Kind);
        return end > localNow ? end - localNow : TimeSpan.Zero;
    }
}
