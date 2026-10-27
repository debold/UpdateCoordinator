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
    [InlineData("10.0.17763.9376", "ServerDatacenter", null, "Windows Server 2019", "2029-01-09", "Extended")]
    [InlineData("10.0.26100.33650", null, "Windows Server 2025 Standard", "Windows Server 2025", "2034-11-14", "Mainstream")]
    [InlineData("10.0.26100.33650", null, null, "Windows Server 2025", "2034-11-14", "Mainstream")]
    [InlineData("10.0.26100.9711", "Professional", "Windows 11 Pro", "Windows 11, version 24H2", "2026-10-13", "Ended")]
    [InlineData("10.0.26100.9711", "Enterprise", "Windows 11 Enterprise", "Windows 11 Enterprise, version 24H2", "2027-10-12", "Mainstream")]
    [InlineData("10.0.19045.6456", "Professional", null, "Windows 10, version 22H2", "2025-10-14", "OutOfSupport")]
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
        Assert.Equal(SupportState.Extended, info.State);
        Assert.Equal("Extended", info.Label);
        Assert.Equal("Extended Support ends 2027-01-12 (in 78 days)", SupportLifecycle.Describe(info));
        Assert.Equal("ok", SupportLifecycle.Class(info.State));
    }

    [Theory]
    [InlineData(true, false, "ESU")]
    [InlineData(null, true, "ESU")]
    [InlineData(false, false, "Out of support")]
    public void AfterTheEnd_EsuFoundByTheAgentCounts(bool? key, bool? azure, string label)
    {
        var info = Create().Of("10.0.19045.6456", "Professional", null, SupportLifecycle.Detected(key, azure));

        Assert.Equal(label, info.Label);
        Assert.Equal(label == "Out of support" ? "fail" : "queue", SupportLifecycle.Class(info.State));
    }

    [Fact]
    public void Tooltips_NameThePhaseWithItsDate_AndTheEsuSource()
    {
        var mainstream = Create().Of("10.0.26100.1", "ServerStandard", null);
        var esu = Create().Of("10.0.19045.6456", "Professional", null, EsuSource.Key);
        var client = Create().Of("10.0.26100.9711", "Enterprise", null);

        Assert.Equal("Mainstream", mainstream.Label);
        Assert.Equal("Mainstream Support ends 2029-11-13 (in 1,114 days); Extended Support until 2034-11-14", SupportLifecycle.Describe(mainstream).Replace('.', ','));
        Assert.Equal("Support ended 2025-10-14 (377 days ago); ESU through a licensed key", SupportLifecycle.Describe(esu));
        Assert.StartsWith("Support ends 2027-10-12", SupportLifecycle.Describe(client), StringComparison.Ordinal);
    }

    [Fact]
    public void AfterTheEnd_UpdatesReleasedSinceMeanEsu_AndMissingThemMeansNone()
    {
        // Windows 10 22H2 ended 2025-10-14; a baseline of 2026-10 is newer.
        var baseline = new SecurityBaselineEntity { Build = 19045, Month = "2026-Oct", ReleasedAtUtc = new DateTime(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc), Revision = 7000 };
        var snapshot = new SecuritySnapshot([baseline], [], []);

        var current = Create().Of("10.0.19045.7000", "Professional", null, EsuSource.None, snapshot.Of("10.0.19045.7000"));
        var behind = Create().Of("10.0.19045.6456", "Professional", null, EsuSource.None, snapshot.Of("10.0.19045.6456"));

        Assert.Equal("ESU", current.Label);
        Assert.EndsWith("; ESU inferred from the security updates installed after the end", SupportLifecycle.Describe(current), StringComparison.Ordinal);
        Assert.Equal(SupportState.OutOfSupport, behind.State);
    }

    [Fact]
    public void Table_IsComplete_AndEveryServerVersionIsListed()
    {
        Assert.All(SupportLifecycle.Entries, e => Assert.True(e.Mainstream is null || e.Mainstream <= e.End, e.Name));
        Assert.Equal([14393, 17763, 20348, 26100], SupportLifecycle.Entries.Where(e => e.Server).Select(e => e.Build));
        Assert.Equal(new DateOnly(2026, 10, 26), SupportLifecycle.AsOf);
    }

    [Fact]
    public void Table_IsReviewedBeforeTheNextWindowsServerIsExpected()
    {
        // Runs with every build, so a release after the expected date stops until the table knows the new version
        // (lifecycle.json: add the version, then move "nextServerRelease" to the one after it).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.True(SupportLifecycle.AsOf < SupportLifecycle.NextServerRelease, "The expected next Windows Server release must lie after the review of the table.");
        Assert.True(today <= SupportLifecycle.NextServerRelease,
            $"A new Windows Server version was expected on {SupportLifecycle.NextServerRelease:yyyy-MM-dd}: review src/SUSyphos.Server/Lifecycle/lifecycle.json (new version, dates, next expected release).");
    }

    [Theory]
    [InlineData("5066835", "https://support.microsoft.com/help/5066835", "https://catalog.update.microsoft.com/Search.aspx?q=KB5066835")]
    [InlineData("2267602", UpdateText.DefenderNotes, "https://catalog.update.microsoft.com/Search.aspx?q=KB2267602")]
    [InlineData("4052623", UpdateText.DefenderNotes, "https://catalog.update.microsoft.com/Search.aspx?q=KB4052623")]
    [InlineData("12345", null, null)]
    [InlineData("50668<script>", null, null)]
    [InlineData(null, null, null)]
    public void KbLinks_OnlyForRealKbNumbers(string? kb, string? notes, string? catalog)
    {
        Assert.Equal(notes, UpdateText.ReleaseNotesLink(kb));
        Assert.Equal(catalog, UpdateText.CatalogLink(kb));
    }

    [Theory]
    [InlineData("5126651", "2026-10 Cumulative Update (KB5126651)", "2026-10 Cumulative Update (KB5126651)")]
    [InlineData("925673", "MSXML 6.0 RTM Security Update (925673)", "KB925673 MSXML 6.0 RTM Security Update (925673)")]
    [InlineData(null, "Broadcom driver", "Broadcom driver")]
    public void Title_NamesTheKbOnce(string? kb, string title, string expected) => Assert.Equal(expected, UpdateText.Title(kb, title));

    [Theory]
    [InlineData("2026-10 Cumulative Update for Windows Server 2022 (KB5066782)", "5066782")]
    [InlineData("Security Intelligence Update - KB2267602 (Version 1.4)", "2267602")]
    [InlineData("Broadcom driver", null)]
    public void KbNumber_IsFoundInTitles(string title, string? kb) => Assert.Equal(kb, UpdateText.KbIn(title));

    [Fact]
    public async Task Inventory_StoresTheEsuSigns_AndKeepsThemWhenAnOldAgentSendsNone()
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);
        var credentials = await RegisterTests.RegisterAsync(browser.Http);
        var inventory = InventoryTests.ValidInventory() with { ExtendedSecurityUpdates = new SUSyphos.Contracts.Agents.ExtendedSecurityUpdatesInfo { KeyLicensed = false, AzureArc = true, Azure = null } };

        using (await InventoryTests.SendAsync(browser.Http, credentials, inventory))
        {
        }

        using (await InventoryTests.SendAsync(browser.Http, credentials, InventoryTests.ValidInventory()))
        {
        }

        var client = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(false, client.EsuKey);
        Assert.Equal(true, client.AzureArc);
        Assert.Null(client.Azure);
        // An Azure Arc agent alone is no proof of ESU.
        Assert.Equal(EsuSource.None, SupportLifecycle.Detected(client.EsuKey, client.Azure));
    }

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
        Assert.StartsWith("Windows Server 2016 on OLD2016: Extended Support ends 2027-01-12", support.Text, StringComparison.Ordinal);
    }
}
