using SUSyphos.Server.Components;

namespace SUSyphos.Server.Tests;

public sealed class DisplayTimeTests
{
    [Fact]
    public void Format_UsesTheTimeZoneOfTheCentralServer_In24HourFormat()
    {
        var utc = new DateTime(2026, 10, 20, 15, 45, 0, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);

        Assert.Equal(local.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), DisplayTime.Format(utc));
        Assert.DoesNotContain("PM", DisplayTime.Format(utc), StringComparison.Ordinal);
        Assert.Equal("–", DisplayTime.Format(null));
    }

    [Fact]
    public void ToUtc_IsTheInverseOfToLocal()
    {
        var utc = new DateTime(2026, 7, 1, 8, 30, 0, DateTimeKind.Utc);

        Assert.Equal(utc, DisplayTime.ToUtc(DisplayTime.ToLocal(utc)));
    }

    [Fact]
    public void ZoneLabel_NamesTheZoneAndItsOffset()
    {
        var label = DisplayTime.ZoneLabel(DateTime.UtcNow);

        Assert.Contains("(UTC", label, StringComparison.Ordinal);
        Assert.Matches(@"\(UTC[+-]\d\d:\d\d\)$", label);
    }
}
