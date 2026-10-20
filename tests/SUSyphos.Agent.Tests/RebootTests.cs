using SUSyphos.Agent.Installation;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class RebootPlannerTests
{
    // Saturday 12:00: inside the default window (Sat/Sun all day).
    private static readonly DateTime Saturday = new(2026, 10, 24, 12, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static RebootDecision Decide(
        RebootPolicy policy = RebootPolicy.InsideMaintenanceWindow,
        InstallationOptions? options = null,
        bool paused = false,
        bool pending = true,
        DateTime? local = null,
        DateTimeOffset? lastRequest = null) =>
        RebootPlanner.Decide(policy, options ?? new InstallationOptions(), paused, pending, MaintenanceWindow.SystemDefault, local ?? Saturday, lastRequest, Now);

    [Fact]
    public void PendingRebootInsideWindow_Reboots() => Assert.Equal(RebootDecision.Reboot, Decide());

    [Fact]
    public void NothingPending_NoReboot() => Assert.Equal(RebootDecision.NotPending, Decide(pending: false));

    [Fact]
    public void PolicyNever_NoReboot() => Assert.Equal(RebootDecision.NotAllowedByPolicy, Decide(RebootPolicy.Never));

    [Fact]
    public void UndefinedPolicy_NoReboot() => Assert.Equal(RebootDecision.NotAllowedByPolicy, Decide((RebootPolicy)7));

    [Fact]
    public void LocalVetoOrDisabledInstallation_NoReboot()
    {
        Assert.Equal(RebootDecision.LocalVeto, Decide(options: new InstallationOptions { AllowReboot = false }));
        Assert.Equal(RebootDecision.LocalVeto, Decide(options: new InstallationOptions { Enabled = false }));
    }

    [Fact]
    public void GlobalPause_NoReboot() => Assert.Equal(RebootDecision.Paused, Decide(paused: true));

    [Fact]
    public void OutsideWindow_NoReboot() => Assert.Equal(RebootDecision.OutsideWindow, Decide(local: new DateTime(2026, 10, 21, 12, 0, 0)));

    [Fact]
    public void WindowEndingWithin30Minutes_NoReboot() =>
        Assert.Equal(RebootDecision.WindowEndsSoon, Decide(local: new DateTime(2026, 10, 21, 4, 40, 0)));

    [Fact]
    public void RecentRequest_NoSecondReboot()
    {
        Assert.Equal(RebootDecision.RecentlyRequested, Decide(lastRequest: Now.AddHours(-5)));
        Assert.Equal(RebootDecision.Reboot, Decide(lastRequest: Now.AddHours(-7)));
    }

    [Fact]
    public void RemainingWindow_FollowsAdjoiningWindows()
    {
        // Saturday 23:50 continues into Sunday all day, which ends Monday 00:00.
        Assert.Equal(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(10), RebootPlanner.RemainingWindow(MaintenanceWindow.SystemDefault, new DateTime(2026, 10, 24, 23, 50, 0)));

        // Monday noon is outside every window; Wednesday 04:40 has 20 minutes left of the Tuesday night window.
        Assert.Null(RebootPlanner.RemainingWindow(MaintenanceWindow.SystemDefault, new DateTime(2026, 10, 26, 12, 0, 0)));
        Assert.Equal(TimeSpan.FromMinutes(20), RebootPlanner.RemainingWindow(MaintenanceWindow.SystemDefault, new DateTime(2026, 10, 21, 4, 40, 0)));
    }
}
