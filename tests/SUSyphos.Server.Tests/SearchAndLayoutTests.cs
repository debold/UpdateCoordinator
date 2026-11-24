using System.Net;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Components.Layout;
using SUSyphos.Server.Components.Pages;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The admin center layout (Marc, 2026-11-24): search of the top bar and the navigation that can be hidden.</summary>
public sealed class SearchAndLayoutTests
{
    private const string Password = "correct horse battery";

    private static async Task<DashboardClient> LoginAsync(ServerFactory factory, string role)
    {
        await factory.CreateUserAsync("user", Password, role);
        var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        return browser;
    }

    [Fact]
    public async Task Search_FindsServersUpdatesPackagesPagesAndHelp_ForTheRoleOnly()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.AddClientAsync("SQL01", SUSyphos.Server.Data.RingEntity.PilotId);
        await scenario.AddClientAsync("SQL02", SUSyphos.Server.Data.RingEntity.PilotId);
        using var reader = await LoginAsync(scenario.Factory, AdminRoles.Reader);

        var html = WebUtility.HtmlDecode(await (await reader.GetAsync("/search?q=sql")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(">SQL01</a>", html, StringComparison.Ordinal);
        Assert.Contains(">SQL02</a>", html, StringComparison.Ordinal);

        // Pages are found by name, administration pages only for administrators.
        var pages = WebUtility.HtmlDecode(await (await reader.GetAsync("/search?q=audit")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("href=\"/administration/audit\"", pages, StringComparison.Ordinal);

        // A wildcard is searched as it is, not as a pattern that matches everything.
        var wildcard = WebUtility.HtmlDecode(await (await reader.GetAsync("/search?q=%25")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(">SQL01</a>", wildcard, StringComparison.Ordinal);
        Assert.Contains("Nothing matches", wildcard, StringComparison.Ordinal);
        Assert.Equal("100\\%\\_a\\\\b", SearchPage.Escape("100%_a\\b"));
    }

    [Fact]
    public async Task Search_OpensASingleMatchDirectly()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("WEB01", SUSyphos.Server.Data.RingEntity.PilotId);
        using var reader = await LoginAsync(scenario.Factory, AdminRoles.Reader);

        using var response = await reader.GetAsync("/search?q=web01");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"/servers/{server.ClientId}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Navigation_IsHiddenAndShownWithACookie_ReturningOnlyToThisSite()
    {
        await using var factory = new ServerFactory();
        using var browser = await LoginAsync(factory, AdminRoles.Reader);

        using (var hidden = await browser.GetAsync($"{NavigationEndpoints.Path}?hidden=1&return=%2Fservers"))
        {
            Assert.Equal(HttpStatusCode.Redirect, hidden.StatusCode);
            Assert.Equal("/servers", hidden.Headers.Location!.ToString());
        }

        Assert.Contains("class=\"app nav-hidden\"", await (await browser.GetAsync("/servers")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using (await browser.GetAsync($"{NavigationEndpoints.Path}?hidden=0&return=%2F"))
        {
        }

        Assert.Contains("class=\"app nav-shown\"", await (await browser.GetAsync("/servers")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        Assert.Equal("/", NavigationEndpoints.SafeReturn("https://evil.example/"));
        Assert.Equal("/", NavigationEndpoints.SafeReturn("//evil.example/"));
        Assert.Equal("/", NavigationEndpoints.SafeReturn("/\\evil.example/"));
        Assert.Equal("/updates?view=all", NavigationEndpoints.SafeReturn("/updates?view=all"));
    }

    [Fact]
    public async Task Help_HasTheTopBar_AlsoBeforeSignIn()
    {
        await using var factory = new ServerFactory();
        using var anonymous = factory.CreateClient();

        var html = await anonymous.GetStringAsync(new Uri("/help", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("<header class=\"top\"", html, StringComparison.Ordinal);
        Assert.Contains("placeholder=\"Search the help\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-my-requests", html, StringComparison.Ordinal);
    }
}
