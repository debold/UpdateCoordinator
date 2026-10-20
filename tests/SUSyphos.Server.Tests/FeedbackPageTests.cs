using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Administration;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Page details from the lab test feedback of 2026-10-20.</summary>
public sealed class FeedbackPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Approvals_ReloadThemselves()
    {
        await using var factory = new ServerFactory();
        using var browser = await LoginAsync(factory, AdminRoles.Operator);

        var html = await (await browser.GetAsync("/approvals")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("http-equiv=\"refresh\" content=\"30\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditLog_FiltersByTarget_AndIgnoresUnknownTargets()
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            await audit.WriteAsync("admin", "Group.Created", "Web", null, CancellationToken.None);
            await audit.WriteAsync("admin", "Group.Created", "Database", null, CancellationToken.None);
        }

        using var browser = await LoginAsync(factory, AdminRoles.Admin);

        var filtered = await (await browser.GetAsync("/administration/audit?f.target=Web")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var unknown = await (await browser.GetAsync("/administration/audit?f.target=%3Cscript%3E")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<td>Web</td>", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("<td>Database</td>", filtered, StringComparison.Ordinal);
        Assert.Contains("No entry matches.", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleSelection_HasASummaryTagPerOption()
    {
        await using var factory = new ServerFactory();
        using var browser = await LoginAsync(factory, AdminRoles.Admin);

        var html = await (await browser.GetAsync("/administration/rules?dialog=new")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("class=\"tag pick\" data-i=\"0\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Roles\" value=\"ClusterNode\" data-i=\"0\"", html, StringComparison.Ordinal);
    }

    private static async Task<DashboardClient> LoginAsync(ServerFactory factory, string role)
    {
        await factory.CreateUserAsync("user", Password, role);
        var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        return browser;
    }
}
