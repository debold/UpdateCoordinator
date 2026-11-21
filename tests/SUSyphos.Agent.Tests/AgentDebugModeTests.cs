using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Agent.Diagnostics;
using SUSyphos.Common.Logging;

namespace SUSyphos.Agent.Tests;

/// <summary>The debug mode of the agent raises the file log level for a while and ends by itself (Marc, 2026-11-21).</summary>
public sealed class AgentDebugModeTests
{
    [Fact]
    public void DebugMode_RaisesTheLevel_UntilItsEnd_AlsoWithoutTheCentralServer()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-21T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var level = new FileLogLevel(LogLevel.Information);
        var mode = new AgentDebugMode(time, NullLogger<AgentDebugMode>.Instance, level);

        mode.Apply(time.GetUtcNow().AddHours(1));
        Assert.True(mode.IsActive);
        Assert.True(level.DebugActive);

        time.Advance(TimeSpan.FromMinutes(61));
        mode.Refresh();
        Assert.False(mode.IsActive);
        Assert.False(level.DebugActive);
        Assert.Null(mode.Until);
    }

    [Fact]
    public void TurnedOff_ReturnsToTheConfiguredLevel()
    {
        var time = new FakeTimeProvider();
        var level = new FileLogLevel(LogLevel.Warning);
        var mode = new AgentDebugMode(time, NullLogger<AgentDebugMode>.Instance, level);
        mode.Apply(time.GetUtcNow().AddHours(4));

        mode.Apply(null);

        Assert.False(level.DebugActive);
        Assert.Equal(LogLevel.Warning, level.Configured);
        Assert.False(level.SetDebug(false));
    }

    [Fact]
    public void EndInThePast_NeverSwitchesOn()
    {
        var time = new FakeTimeProvider();
        var level = new FileLogLevel(LogLevel.Information);
        var mode = new AgentDebugMode(time, NullLogger<AgentDebugMode>.Instance, level);

        mode.Apply(time.GetUtcNow().AddMinutes(-5));

        Assert.False(mode.IsActive);
        Assert.False(level.DebugActive);
    }
}
