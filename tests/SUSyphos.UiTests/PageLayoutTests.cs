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
        "/operating-systems",
        "/servers/{main}?tab=security",
        "/updates",
        "/updates/{update}",
        "/updates/{update}?tab=servers",
        "/updates/{update}?tab=timeline",
        "/updates/{update}?dialog=uninstall",
        "/approvals",
        "/agents",
        "/coverage",
        "/administration/users",
        "/administration/users?dialog=new",
        "/administration/groups",
        "/administration/groups?dialog=new",
        "/administration/rules",
        "/administration/rules?dialog=new",
        "/administration/release-rules",
        "/administration/release-rules?tab=rings",
        "/administration/agent-settings",
        "/administration/release-rules?dialog=rule-new",
        "/administration/maintenance",
        "/administration/certificate",
        "/administration/notifications",
        "/administration/notifications?tab=events",
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

    /// <summary>Every "Group by" a list offers shows group rows (it once did nothing on the update list).</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryGrouping_ShowsGroupRows(string path)
    {
        await using var context = await ui.NewContextAsync();
        var page = await context.NewPageAsync();
        var url = await ResolveAsync(path);
        await page.GotoAsync(url);

        var choices = await page.EvalOnSelectorAllAsync<string[][]>(
            "main select[id$='table-group']",
            "selects => selects.flatMap(s => [...s.options].filter(o => o.value).map(o => [s.name, o.value]))");
        var missing = new List<string>();
        foreach (var choice in choices)
        {
            var grouped = new UriBuilder(new Uri(ui.BaseAddress, url));
            var query = System.Web.HttpUtility.ParseQueryString(grouped.Query);
            query[choice[0]] = choice[1];
            grouped.Query = query.ToString();
            await page.GotoAsync(grouped.Uri.ToString());
            var select = page.Locator($"main select[name='{choice[0]}']");
            var groups = await select.EvaluateAsync<int>("s => (s.closest('section, .box') ?? document).querySelectorAll('tr.group-row').length");
            var rows = await select.EvaluateAsync<int>("s => (s.closest('section, .box') ?? document).querySelectorAll('tbody tr:not(.group-row)').length");
            if (rows > 0 && groups == 0)
            {
                missing.Add($"{choice[0]}={choice[1]}");
            }
        }

        Assert.True(missing.Count == 0, $"{path}: grouping shows no group rows for {string.Join(", ", missing)}");
    }

    /// <summary>Every drop-down and row menu opens fully visible: not cut off by a table's scroll container, inside the window.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryMenu_OpensVisibly(string path)
    {
        await using var context = await ui.NewContextAsync(1280, 800);
        var page = await context.NewPageAsync();
        await page.GotoAsync(await ResolveAsync(path));

        var hidden = new List<string>();
        // With a modal dialog open only its own menus can be used.
        var scope = await page.Locator("[role=dialog][aria-modal=true]").CountAsync() > 0 ? "[role=dialog][aria-modal=true]" : "main";
        var menus = page.Locator($"{scope} details[data-menu]:visible > summary");
        var count = await menus.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var summary = menus.Nth(i);
            await summary.ClickAsync();
            var problem = await summary.EvaluateAsync<string?>("""
                async summary => {
                    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                    const pop = summary.parentElement.querySelector(':scope > .menu-pop');
                    const name = (summary.getAttribute('aria-label') || summary.textContent).trim();
                    const r = pop.getBoundingClientRect();
                    if (r.width === 0 || r.height === 0) return `${name}: not shown`;
                    if (r.left < 0 || r.top < 0 || r.right > document.documentElement.clientWidth || r.bottom > window.innerHeight) return `${name}: outside the window`;
                    for (const [x, y] of [[r.left + 4, r.top + 4], [r.right - 4, r.bottom - 4], [(r.left + r.right) / 2, (r.top + r.bottom) / 2]]) {
                        const hit = document.elementFromPoint(x, y);
                        if (!hit || !pop.contains(hit)) return `${name}: covered or cut off at ${Math.round(x)},${Math.round(y)}`;
                    }
                    return null;
                }
                """);
            if (problem is not null)
            {
                hidden.Add(problem);
            }

            await page.Keyboard.PressAsync("Escape");
        }

        Assert.True(hidden.Count == 0, $"{path} has menus that do not open visibly:\n{string.Join('\n', hidden)}");
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
