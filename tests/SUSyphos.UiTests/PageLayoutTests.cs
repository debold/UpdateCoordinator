using System.Globalization;

using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// Every dashboard page in several window sizes, checked in a real browser: layout rules (<see cref="LayoutAudit"/>), no
/// script or content-security-policy errors, and every link on the page leads somewhere. A failing page leaves a
/// screenshot in the test output folder (ui-failures).
/// </summary>
public sealed class PageLayoutTests(UiFixture ui)
{
    public static readonly TheoryData<string> Pages =
    [
        "/",
        "/?stat=errors",
        "/?stat=new-updates&days=365",
        "/servers",
        "/servers?group=os",
        "/servers/{main}",
        "/servers/{main}?tab=updates",
        "/servers/{main}?tab=history",
        "/servers/{main}?tab=configuration",
        "/servers/{main}?tab=system",
        "/servers/{main}?tab=activity",
        "/servers/{main}?dialog=restart",
        "/servers/{main}?dialog=install",
        "/servers/{main}?dialog=uninstall",
        "/servers/{main}?dialog=remove",
        "/servers/{main}?dialog=agent-settings",
        "/servers/{main}?dialog=agent-setting&key=heartbeat-interval",
        "/servers/{main}?dialog=agent-setting&key=log-level",
        "/updates",
        "/updates/{update}",
        "/updates/{update}?dialog=uninstall",
        "/approvals",
        "/agents",
        "/coverage",
        "/administration/users",
        "/administration/users?dialog=new",
        "/administration/groups",
        "/administration/groups?dialog=new",
        "/administration/rules",
        "/administration/release-rules",
        "/administration/agent-settings",
        "/administration/release-rules?dialog=rule-new",
        "/administration/maintenance",
        "/administration/certificate",
        "/administration/notifications",
        "/administration/backup",
        "/administration/audit",
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Page_FollowsTheLayoutRules_InEveryWindowSize(string path)
    {
        var failures = new List<string>();
        foreach (var (width, height) in new[] { (1920, 1080), (1440, 900), (1280, 800), (1024, 768) })
        {
            await using var context = await ui.NewContextAsync(width, height);
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    errors.Add(message.Text);
                }
            };
            page.PageError += (_, error) => errors.Add(error);

            var url = await ResolveAsync(path);
            var response = await page.GotoAsync(url);
            Assert.NotNull(response);
            if (!response.Ok)
            {
                failures.Add($"{width}px: HTTP {response.Status}");
                continue;
            }

            var issues = (await LayoutAudit.RunAsync(page, width)).Concat(errors.Select(e => $"browser error: {e}")).ToList();
            if (issues.Count > 0)
            {
                failures.Add($"{width}px:\n  " + string.Join("\n  ", issues));
                await SaveScreenshotAsync(page, path, width);
            }
        }

        Assert.True(failures.Count == 0, $"{path}\n{string.Join('\n', failures)}");
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryLinkOnThePage_LeadsSomewhere(string path)
    {
        await using var context = await ui.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(await ResolveAsync(path));

        var links = await page.EvalOnSelectorAllAsync<string[]>("main a[href]", "links => links.map(a => a.href)");
        var broken = new List<string>();
        foreach (var link in links.Distinct().Where(l => l.StartsWith(ui.BaseAddress.ToString(), StringComparison.Ordinal) && !l.Contains("/logout", StringComparison.Ordinal) && !l.Contains("/download", StringComparison.Ordinal)))
        {
            var response = await context.APIRequest.GetAsync(link, new APIRequestContextOptions { MaxRedirects = 0 });
            if (response.Status >= 400)
            {
                broken.Add($"{response.Status} {link}");
            }
        }

        Assert.True(broken.Count == 0, $"{path} has broken links:\n{string.Join('\n', broken)}");
    }

    /// <summary>A named field outside every form is never sent; it looks saved and silently is not.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryField_BelongsToAForm(string path)
    {
        await using var context = await ui.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(await ResolveAsync(path));

        var orphans = await page.EvalOnSelectorAllAsync<string[]>(
            "main input[name], main select[name], main textarea[name], main button[name]",
            "fields => fields.filter(f => !f.form).map(f => f.tagName.toLowerCase() + '[name=' + f.name + ']')");

        Assert.True(orphans.Length == 0, $"{path} has fields outside every form:\n{string.Join('\n', orphans)}");
    }

    /// <summary>Fills the ids of the demo lab into a path.</summary>
    internal async Task<string> ResolveAsync(string path)
    {
        if (path.Contains("{update}", StringComparison.Ordinal))
        {
            await using var context = await ui.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync("/updates");
            var href = await page.GetAttributeAsync("main a[href^='/updates/']", "href");
            path = path.Replace("/updates/{update}", href ?? "/updates", StringComparison.Ordinal);
        }

        return path.Replace("{main}", ui.MainServerId.ToString(), StringComparison.Ordinal);
    }

    private static async Task SaveScreenshotAsync(IPage page, string path, int width)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "ui-failures");
        Directory.CreateDirectory(folder);
        var name = string.Concat(path.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, $"{(name.Length == 0 ? "overview" : name)}-{width.ToString(CultureInfo.InvariantCulture)}.png"), FullPage = true });
    }
}
