using SUSyphos.Server.Administration;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The lists of the release rules page (Marc, 2026-11-13): conditions in one column, compact transitions.</summary>
public sealed class ReleaseRulesPageTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task StartRules_ShowTheirConditionsInOneColumn_AndRingsCompactly()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var rules = await (await browser.GetAsync("/administration/release-rules")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var rings = await (await browser.GetAsync("/administration/release-rules?tab=rings")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains(">Conditions", rules, StringComparison.Ordinal);
        Assert.DoesNotContain(">Pattern<", rules, StringComparison.Ordinal);
        Assert.Contains("all updates", rules, StringComparison.Ordinal);
        Assert.Contains("<span class=\"pill neutral\">Final ring</span>", rings, StringComparison.Ordinal);
        Assert.Contains("feedback ≥ 80 %", rings, StringComparison.Ordinal);
    }
}
