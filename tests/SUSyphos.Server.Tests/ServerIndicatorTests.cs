using SUSyphos.Server.Administration;
using SUSyphos.Server.AgentPackages;

namespace SUSyphos.Server.Tests;

public sealed class ServerIndicatorTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, true, null, (int)AgentState.Current)]
    [InlineData(null, true, "0.7.6", (int)AgentState.UpdateAvailable)]
    [InlineData("msiexec exit code 1603", true, "0.7.6", (int)AgentState.Failed)]
    [InlineData(null, false, null, (int)AgentState.Other)]
    public void AgentState_FollowsErrorVersionAndTarget(string? error, bool known, string? target, int expected) =>
        Assert.Equal((AgentState)expected, ServerIndicators.Agent(error, known, target));

    [Theory]
    [InlineData(0, (int)Freshness.Current)]
    [InlineData(5, (int)Freshness.Current)]
    [InlineData(6, (int)Freshness.Older)]
    [InlineData(60, (int)Freshness.Older)]
    [InlineData(61, (int)Freshness.Stale)]
    public void Heartbeat_IsCurrentOlderOrStale(int minutesAgo, int expected) =>
        Assert.Equal((Freshness)expected, ServerIndicators.Heartbeat(Now.AddMinutes(-minutesAgo), Now));

    private static AgentVersion Version(string text) => AgentVersion.TryParse(text, out var version) ? version! : throw new ArgumentException(text);

    [Fact]
    public void NoHeartbeat_IsStale() => Assert.Equal(Freshness.Stale, ServerIndicators.Heartbeat(null, Now));

    [Fact]
    public void Target_IsTheNewestReleasedForTheRing_AndNoneForCurrentAgents()
    {
        List<AgentPackageEntity> packages =
        [
            new() { Version = "0.7.5", ReleasedThroughRingOrder = 3, Source = AgentPackageSource.GitHub, FileName = "a.msi", Sha256 = "00" },
            new() { Version = "0.7.6", ReleasedThroughRingOrder = 1, Source = AgentPackageSource.GitHub, FileName = "b.msi", Sha256 = "00" },
        ];
        var minimum = AgentUpdateService.BuiltInMinimum;

        Assert.Equal("0.7.6", AgentUpdateService.TargetFor(packages, 1, Version("0.7.4"), minimum)?.Version);
        Assert.Equal("0.7.5", AgentUpdateService.TargetFor(packages, 3, Version("0.7.4"), minimum)?.Version);
        Assert.Null(AgentUpdateService.TargetFor(packages, 3, Version("0.7.5"), minimum));
    }
}
