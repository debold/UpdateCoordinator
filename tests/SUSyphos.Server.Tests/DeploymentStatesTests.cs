using SUSyphos.Server.Rollouts;
using SUSyphos.Server.Components;

namespace SUSyphos.Server.Tests;

/// <summary>The deployment states have one name, colour and explanation everywhere (Marc, 2026-11-20).</summary>
public sealed class DeploymentStatesTests
{
    public static readonly TheoryData<int, string, string> States = new()
    {
        { (int)DeploymentState.Successful, "Installed", "ok" },
        { (int)DeploymentState.Pending, "Ready to install", "pend" },
        { (int)DeploymentState.Queued, "Waiting for release", "wait" },
        { (int)DeploymentState.Failed, "Failed", "fail" },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void State_HasItsNameAndColour_InChartsAndLinks(int value, string label, string cls)
    {
        var state = (DeploymentState)value;
        Assert.Equal((label, cls), (DeploymentStates.Label(state), DeploymentStates.Class(state)));
        Assert.Equal(label, RingLink.Label(state));
        Assert.Contains(Charts.Labeled(new DeploymentCounts(1, 1, 1, 1)), p => p.Label == label && p.Class == cls && p.Explanation == DeploymentStates.Explanation(state));
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Help_UsesTheSameSentenceAsTheTooltip(int value, string label, string cls)
    {
        _ = cls;
        var help = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SUSyphos.Server", "Help", "Topics", "10-overview.md"));
        Assert.Contains($"**{label}**", help, StringComparison.Ordinal);
        Assert.Contains(DeploymentStates.Explanation((DeploymentState)value), help, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitingIsNotTheAmberOfAttention() => Assert.NotEqual("queue", DeploymentStates.Class(DeploymentState.Queued));
}
