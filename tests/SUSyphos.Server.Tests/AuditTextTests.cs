using SUSyphos.Server.Components;

namespace SUSyphos.Server.Tests;

/// <summary>How audit entries read in the dashboard (Marc, 2026-11-13).</summary>
public sealed class AuditTextTests
{
    [Theory]
    [InlineData("Action.LogsDownloaded", "Remote action", "Logs downloaded")]
    [InlineData("Rollout.AlarmCleared", "Rollout", "Alarm cleared")]
    [InlineData("Package.Removed", "Software", "Removed")]
    [InlineData("Settings.Notifications", "Settings", "Notifications")]
    public void Action_IsShownAsAreaAndWords(string action, string area, string words)
    {
        Assert.Equal(area, AuditText.Area(action));
        Assert.Equal(words, AuditText.Readable(action));
    }

    [Theory]
    [InlineData("LABVEX01 (371f3c55-2002-4457-bc70-899bcfdf4806)", "LABVEX01", "371f3c55-2002-4457-bc70-899bcfdf4806")]
    [InlineData("7795926b-52cb-4a78-ae9b-6730ad9aab2c", "", "7795926b-52cb-4a78-ae9b-6730ad9aab2c")]
    [InlineData("4 results", "4 results", null)]
    public void Ids_MoveToTheirOwnLine(string value, string text, string? id)
    {
        Assert.Equal((text, id), AuditText.SplitId(value));
    }

    [Fact]
    public void RuleAndInventory_AreAutomatic_PeopleAreNot()
    {
        Assert.True(AuditText.IsAutomatic("rule"));
        Assert.False(AuditText.IsAutomatic(@"AD\adm.debold"));
    }
}
