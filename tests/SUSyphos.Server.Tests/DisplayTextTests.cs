using SUSyphos.Server.Administration;
using SUSyphos.Server.Components;

namespace SUSyphos.Server.Tests;

public sealed class DisplayTextTests
{
    [Theory]
    [InlineData(1, "1 server")]
    [InlineData(0, "0 servers")]
    [InlineData(2, "2 servers")]
    public void Count_PicksSingularOnlyForOne(int value, string expected)
    {
        Assert.Equal(expected, DisplayNumber.Count(value, "server", "servers"));
    }

    [Fact]
    public void LegendLabel_ShortensRebootPending_AndKeepsTheOthers()
    {
        Assert.Equal("Reboot", ServerHealth.LegendLabel(ServerHealthState.RebootPending));
        Assert.Equal("Reboot pending", ServerHealth.Label(ServerHealthState.RebootPending));
        Assert.Equal(ServerHealth.Label(ServerHealthState.Failed), ServerHealth.LegendLabel(ServerHealthState.Failed));
    }
}
