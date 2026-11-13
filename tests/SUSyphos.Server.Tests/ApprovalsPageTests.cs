using SUSyphos.Server.Administration;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The Approvals page as an operator sees it.</summary>
public sealed class ApprovalsPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task WithoutWaitingServers_TheServerDecisionsAreDisabled_WithTheReason()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        var html = await (await browser.GetAsync("/approvals")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("value=\"approve-selected\" class=\"primary\" disabled title=\"No server is waiting for approval\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"reject-selected\" class=\"danger\" disabled", html, StringComparison.Ordinal);
    }
}
