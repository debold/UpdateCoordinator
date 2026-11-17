using System.Net;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Components;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.Server.Tests;

/// <summary>
/// The parts of the ring bar on the overview link to the update list filtered to the ring and a state there (Marc,
/// 2026-11-17); the list then counts only the servers of the ring.
/// </summary>
public sealed class RingLinkTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task RingBar_LinksToTheUpdatesOfTheRing_AndTheListCountsOnlyItsServers()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var first = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var second = await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        var failing = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), KbArticle = "5040001", Title = "Failing update (KB5040001)" };
        var fine = RolloutScenario.Update("Security Updates") with { UpdateId = Guid.NewGuid(), KbArticle = "5040002", Title = "Pending update (KB5040002)" };
        await scenario.ReportAsync(first, failing, fine);
        await scenario.ReportAsync(second, failing, fine);
        await scenario.ReportAsync(production, failing, fine);
        await scenario.InstallAsync(first, failing.UpdateId, InstallResultCode.Failed);
        await scenario.InstallAsync(second, failing.UpdateId, InstallResultCode.Failed);

        await scenario.Factory.CreateUserAsync("reader", Password, SUSyphos.Server.Administration.AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        // Two failed pairs on one update: the text says both, and the red part links to the list.
        var overview = WebUtility.HtmlDecode(await (await browser.GetAsync("/")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(RingLink.Link("Pilot", DeploymentState.Failed), overview, StringComparison.Ordinal);
        Assert.Contains("2 failed on 1 update", overview, StringComparison.Ordinal);
        Assert.Contains(RingLink.Link("Pilot", DeploymentState.Pending), overview, StringComparison.Ordinal);

        var failed = WebUtility.HtmlDecode(await (await browser.GetAsync(RingLink.Link("Pilot", DeploymentState.Failed))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Failing update (KB5040001)", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("Pending update (KB5040002)", failed, StringComparison.Ordinal);

        Assert.Contains("In Pilot: Failed", failed, StringComparison.Ordinal);
        Assert.Contains("Ring: 1 Pilot (2 servers)", failed, StringComparison.Ordinal);
        Assert.Contains("0/2, 2 failed", failed, StringComparison.Ordinal);

        // The whole ring: both updates; without the ring filter the status counts every server again.
        var ring = WebUtility.HtmlDecode(await (await browser.GetAsync(RingLink.Link("pilot"))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Failing update (KB5040001)", ring, StringComparison.Ordinal);
        Assert.Contains("Pending update (KB5040002)", ring, StringComparison.Ordinal);
        Assert.DoesNotContain("In Pilot:", ring, StringComparison.Ordinal);

        var all = WebUtility.HtmlDecode(await (await browser.GetAsync("/updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("0/3, 2 failed", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Ring: ", all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/updates?ring=Nowhere&ring-state=failed")]
    [InlineData("/updates?ring=%27%3Bdrop&ring-state=%3Cscript%3E")]
    [InlineData("/updates?ring-state=failed")]
    public async Task UnknownRingOrState_IsIgnored(string path)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("reader", Password, SUSyphos.Server.Administration.AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        using var response = await browser.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Ring: ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ok", (int)DeploymentState.Successful)]
    [InlineData("pend", (int)DeploymentState.Pending)]
    [InlineData("queue", (int)DeploymentState.Queued)]
    [InlineData("fail", (int)DeploymentState.Failed)]
    public void SegmentClass_NamesItsState_AndTheLinkReadsBack(string segment, int value)
    {
        var state = (DeploymentState)value;
        Assert.Equal(state, RingLink.StateOfClass(segment));
        var link = RingLink.Link("Pilot Ring", state);
        Assert.StartsWith("/updates?ring=Pilot%20Ring&ring-state=", link, StringComparison.Ordinal);
        Assert.Equal(state, RingLink.Parse(link[(link.LastIndexOf('=') + 1)..]));
    }

    [Fact]
    public void NotNeeded_AndUnknownValues_HaveNoState()
    {
        Assert.Null(RingLink.StateOfClass("none"));
        Assert.Null(RingLink.Parse("everything"));
        Assert.Null(RingLink.Parse(null));
    }
}
