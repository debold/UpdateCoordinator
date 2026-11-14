using SUSyphos.Common.Diagnostics;

namespace SUSyphos.Agent.Tests;

public sealed class EventLogExtractTests
{
    [Fact]
    public void Query_NamesOnlyTheFixedSources_AndTheTimeInUtc()
    {
        var query = EventLogExtract.Query(new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc));

        Assert.Contains("Provider[@Name='MsiInstaller']", query, StringComparison.Ordinal);
        Assert.Contains("Provider[@Name='SUSyphos.Agent']", query, StringComparison.Ordinal);
        Assert.Contains("TimeCreated[@SystemTime>='2026-11-14T06:00:00.000Z']", query, StringComparison.Ordinal);
        Assert.Equal(EventLogExtract.Providers.Count, query.Split("Provider[").Length - 1);
    }

    [Fact]
    public void UnknownLog_GivesAText_InsteadOfAnException()
    {
        var text = EventLogExtract.Read(DateTime.UtcNow.AddDays(-1), "SUSyphos-NoSuchLog-" + Guid.NewGuid().ToString("N"));

        Assert.Contains("could not be read", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationLog_IsReadWithAHeaderAndACount()
    {
        // Reading needs no rights beyond those of a normal user and changes nothing on the machine.
        var text = EventLogExtract.Read(DateTime.UtcNow.AddHours(-1));

        Assert.StartsWith("Event log Application since ", text, StringComparison.Ordinal);
        Assert.Matches(@"(\d+ entries\.|Stopped after \d+ entries\.|could not be read)", text);
    }
}
