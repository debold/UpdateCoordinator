using System.Net;
using System.Text.RegularExpressions;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>
/// Follows every dialog link of the administration pages the way a browser does (HTML-decoding the href once), so a link
/// that opens no dialog in a real browser fails here too.
/// </summary>
public sealed partial class DialogLinkTests
{
    private const string Password = "correct horse battery";

    [Theory]
    [InlineData("/administration/users")]
    [InlineData("/administration/groups")]
    [InlineData("/administration/rules")]
    [InlineData("/administration/release-rules")]
    public async Task EveryDialogLink_OpensItsDialog(string page)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        await factory.CreateUserAsync("second", Password, AdminRoles.Reader);
        await factory.WithDbAsync(db =>
        {
            db.Groups.Add(new GroupEntity { Id = 30, Name = "Web" });
            db.AssignmentRules.Add(new AssignmentRuleEntity { Priority = 10, Target = AssignmentTarget.Group, TargetId = 30, Domain = "example.local" });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var html = await (await admin.GetAsync(page)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var links = HrefRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).Distinct().ToList();

        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            var target = await (await admin.GetAsync(link)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(target.Contains("role=\"dialog\"", StringComparison.Ordinal), $"The link {link} on {page} opens no dialog.");
        }
    }

    [GeneratedRegex("href=\"([^\"]*\\?dialog=[^\"]*)\"")]
    private static partial Regex HrefRegex();
}
