using SUSyphos.Server.Administration;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The Approvals page as an operator sees it.</summary>
public sealed class ApprovalsPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task WithoutWaitingServers_NoDecisionIsOffered()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        var html = await (await browser.GetAsync("/approvals")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Marc, 2026-11-21: the decisions sit in a bar above the list; without waiting servers there is no list and no bar.
        Assert.Contains("No server is waiting for approval.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"approve-selected\"", html, StringComparison.Ordinal);
    }
}
