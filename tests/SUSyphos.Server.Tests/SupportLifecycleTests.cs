using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Lifecycle;
using SUSyphos.Server.Notifications;
using SUSyphos.Server.Rollouts;
using SUSyphos.Server.Security;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class SupportLifecycleTests
{
    private const string Password = "correct horse battery";

    private static SupportLifecycle Create(int warning = 180) =>
        new(Options.Create(new LifecycleOptions { WarningDays = warning }), new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData("10.0.14393.9612", "ServerStandard", null, "Windows Server 2016", "2027-01-12", "EndingSoon")]
    [InlineData("10.0.17763.9376", "ServerDatacenter", null, "Windows Server 2019", "2029-01-09", "SecurityOnly")]
    [InlineData("10.0.26100.33650", null, "Windows Server 2025 Standard", "Windows Server 2025", "2034-11-14", "Supported")]
    [InlineData("10.0.26100.33650", null, null, "Windows Server 2025", "2034-11-14", "Supported")]
    [InlineData("10.0.26100.9711", "Professional", "Windows 11 Pro", "Windows 11, version 24H2", "2026-10-13", "Ended")]
    [InlineData("10.0.26100.9711", "Enterprise", "Windows 11 Enterprise", "Windows 11 Enterprise, version 24H2", "2027-10-12", "Supported")]
    [InlineData("10.0.19045.6456", "Professional", null, "Windows 10, version 22H2", "2025-10-14", "EndOfSupport")]
    public void KnownVersions_GetTheirEndOfSupport_AndAState(string version, string? edition, string? product, string name, string end, string state)
    {
        var info = Create().Of(version, edition, product);

        Assert.Equal(name, info.Name);
        Assert.Equal(DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), info.End);
        Assert.Equal(state, info.State.ToString());
    }

    [Theory]
    [InlineData("10.0.9999.1")]
    [InlineData("not a version")]
    [InlineData(null)]
    public void UnknownVersions_HaveNoDates(string? version)
    {
        var info = Create().Of(version, "ServerStandard", null);

        Assert.Equal(SupportState.Unknown, info.State);
        Assert.Null(info.End);
        Assert.Equal("–", SupportLifecycle.Describe(info));
    }

    [Fact]
    public void WarningDays_DecideWhenEndingSoonStarts()
    {
        var info = Create(warning: 60).Of("10.0.14393.1", "ServerStandard", null);

        Assert.Equal(78, info.DaysLeft);
        Assert.Equal(SupportState.SecurityOnly, info.State);
        Assert.Equal("Security only", info.Label);
        Assert.Equal("ends 2027-01-12 (in 78 days)", SupportLifecycle.Describe(info));
        Assert.Equal("ok", SupportLifecycle.Class(info.State));
    }

    [Theory]
    [InlineData(true, false, false, "ESU (key)")]
    [InlineData(false, true, false, "ESU (Azure Arc)")]
    [InlineData(null, null, true, "ESU (Azure)")]
    [InlineData(false, false, false, "No ESU")]
    public void AfterTheEnd_EsuFoundByTheAgentCounts(bool? key, bool? arc, bool? azure, string label)
    {
        var info = Create().Of("10.0.19045.6456", "Professional", null, SupportLifecycle.Detected(key, arc, azure));

        Assert.Equal(label, info.Label);
        Assert.Equal(label == "No ESU" ? "fail" : "queue", SupportLifecycle.Class(info.State));
    }

    [Fact]
    public void AfterTheEnd_UpdatesReleasedSinceMeanEsu_AndMissingThemMeansNone()
    {
        // Windows 10 22H2 ended 2025-10-14; a baseline of 2026-10 is newer.
        var baseline = new SecurityBaselineEntity { Build = 19045, Month = "2026-Oct", ReleasedAtUtc = new DateTime(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc), Revision = 7000 };
        var snapshot = new SecuritySnapshot([baseline], [], []);

        var current = Create().Of("10.0.19045.7000", "Professional", null, EsuSource.None, snapshot.Of("10.0.19045.7000"));
        var behind = Create().Of("10.0.19045.6456", "Professional", null, EsuSource.None, snapshot.Of("10.0.19045.6456"));

        Assert.Equal("ESU, inferred from updates", current.Label);
        Assert.Equal(SupportState.EndOfSupport, behind.State);
    }

    [Fact]
    public void Table_IsComplete_AndEveryServerVersionIsListed()
    {
        Assert.All(SupportLifecycle.Entries, e => Assert.True(e.Mainstream is null || e.Mainstream <= e.End, e.Name));
        Assert.Equal([14393, 17763, 20348, 26100], SupportLifecycle.Entries.Where(e => e.Server).Select(e => e.Build));
        Assert.Equal(new DateOnly(2026, 10, 26), SupportLifecycle.AsOf);
    }

    [Theory]
    [InlineData("5066835", "https://support.microsoft.com/help/5066835", "https://catalog.update.microsoft.com/Search.aspx?q=KB5066835")]
    [InlineData("12345", null, null)]
    [InlineData("50668<script>", null, null)]
    [InlineData(null, null, null)]
    public void KbLinks_OnlyForRealKbNumbers(string? kb, string? notes, string? catalog)
    {
        Assert.Equal(notes, UpdateText.ReleaseNotesLink(kb));
        Assert.Equal(catalog, UpdateText.CatalogLink(kb));
    }

    [Theory]
    [InlineData("2026-10 Cumulative Update for Windows Server 2022 (KB5066782)", "5066782")]
    [InlineData("Security Intelligence Update - KB2267602 (Version 1.4)", "2267602")]
    [InlineData("Broadcom driver", null)]
    public void KbNumber_IsFoundInTitles(string title, string? kb) => Assert.Equal(kb, UpdateText.KbIn(title));

    [Fact]
    public async Task Pages_ShowSupport_AndTheOverviewAndNotificationsWarn()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await scenario.AddClientAsync("OLD2016", RingEntity.PilotId);
        await scenario.AddClientAsync("NEW2025", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == old.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.OsVersion, "10.0.14393.9612").SetProperty(c => c.OsEditionId, "ServerStandard"), TestContext.Current.CancellationToken));
        await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.HostName == "NEW2025").ExecuteUpdateAsync(s => s.SetProperty(c => c.OsVersion, "10.0.26100.33650").SetProperty(c => c.OsEditionId, "ServerDatacenter"), TestContext.Current.CancellationToken));
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        // The clock of the test runs today; Server 2016 ends on 2027-01-12, Server 2025 in 2034.
        var expected = new SupportLifecycle(Options.Create(new LifecycleOptions()), TimeProvider.System).Of("10.0.14393.9612", "ServerStandard", null);
        var list = await (await reader.GetAsync("/servers?f.support=" + Uri.EscapeDataString(SupportLifecycle.Label(expected.State)))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var system = await (await reader.GetAsync($"/servers/{old.ClientId}?tab=system")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var overview = await (await reader.GetAsync("/")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("OLD2016", list, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW2025", list, StringComparison.Ordinal);
        Assert.Contains("2027-01-12", list, StringComparison.Ordinal);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(SupportLifecycle.Describe(expected)), system, StringComparison.Ordinal);
        Assert.Contains("mainstream support ended 2022-01-11", system, StringComparison.Ordinal);
        Assert.Contains("f.support=", overview, StringComparison.Ordinal);

        var events = await scenario.WithServiceAsync<NotificationEvents, List<NotificationEvent>>(e => e.CollectAsync(CancellationToken.None));
        var support = Assert.Single(events, e => e.Kind == NotificationKind.SupportEnding);
        Assert.Equal("OLD2016", support.Affected);
        Assert.StartsWith("Windows Server 2016 on OLD2016 ends 2027-01-12", support.Text, StringComparison.Ordinal);
    }
}
