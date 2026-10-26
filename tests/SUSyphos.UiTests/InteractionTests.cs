using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// Behaviour in the browser: dialogs open and close, column filters stay in the window and can be searched, dependent
/// fields switch with their switch, and the maintenance window preview follows every change. No form is submitted, so
/// the shared demo lab stays unchanged.
/// </summary>
public sealed class InteractionTests(UiFixture ui)
{
    [Theory]
    [InlineData("/servers/{main}")]
    [InlineData("/administration/users")]
    [InlineData("/administration/groups")]
    [InlineData("/administration/release-rules")]
    public async Task DialogLinks_OpenAVisibleDialog_ThatFitsTheWindow_AndCloses(string path)
    {
        await using var context = await ui.NewContextAsync(1280, 800);
        var page = await context.NewPageAsync();
        var start = path.Replace("{main}", ui.MainServerId.ToString(), StringComparison.Ordinal);
        await page.GotoAsync(start);

        var links = await page.EvalOnSelectorAllAsync<string[]>("main a[href*='dialog=']", "links => [...new Set(links.map(a => a.getAttribute('href')))]");
        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            await page.GotoAsync(start);

            // Links in a collapsed section (agent settings) are opened the way a user would.
            await page.EvalOnSelectorAsync($"main a[href='{link}']", "a => { const section = a.closest('details'); if (section) section.open = true; }");
            await page.ClickAsync($"main a[href='{link}']");
            var dialog = page.Locator("[role='dialog']");
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            var box = await dialog.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box.X >= 0 && box.X + box.Width <= 1280 + 1, $"{link}: the dialog is wider than the window ({box.X}+{box.Width}).");

            var close = dialog.Locator("a.btn", new LocatorLocatorOptions { HasTextString = "Cancel" });
            if (await close.CountAsync() > 0)
            {
                await close.First.ClickAsync();
                await Assertions.Expect(page.Locator("[role='dialog']")).ToHaveCountAsync(0);
            }
        }
    }

    [Theory]
    [InlineData("/servers", 1024)]
    [InlineData("/servers", 1440)]
    [InlineData("/updates", 1280)]
    public async Task ColumnFilters_OpenInsideTheWindow(string path, int width)
    {
        await using var context = await ui.NewContextAsync(width, 800);
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);

        var filters = page.Locator("details.col-filter");
        var count = await filters.CountAsync();
        Assert.True(count > 0);
        for (var i = 0; i < count; i++)
        {
            await filters.Nth(i).Locator("summary").ClickAsync();
            var pop = filters.Nth(i).Locator(".filter-pop");
            await Assertions.Expect(pop).ToBeVisibleAsync();
            // The script moves a filter that would open beyond the edge back into view once it opened (toggle event).
            await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
            var box = await pop.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box.X >= 0 && box.X + box.Width <= width + 1, $"Filter {i} on {path} opens outside the window ({box.X}+{box.Width} > {width}).");
            await filters.Nth(i).Locator("summary").ClickAsync();
        }
    }

    [Fact]
    public async Task FilterSearch_NarrowsTheCriteria()
    {
        await using var context = await ui.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/servers");

        // The server column filters by domain; the demo lab has more than six status values only with search shown.
        var filter = page.Locator("details.col-filter").Filter(new LocatorFilterOptions { Has = page.Locator(".filter-search") }).First;
        if (await filter.CountAsync() == 0)
        {
            Assert.Skip("No filter in the demo lab has enough criteria for a search box.");
        }

        await filter.Locator("summary").ClickAsync();
        var search = filter.Locator(".filter-search");
        await Assertions.Expect(search).ToBeVisibleAsync();
        var labels = filter.Locator(".filter-options label");
        var before = await labels.Locator(":visible").CountAsync();
        var first = (await labels.First.InnerTextAsync()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        await search.FillAsync(first);
        var after = await filter.Locator(".filter-options label:visible").CountAsync();

        Assert.True(after >= 1 && after < before, $"Searching for '{first}' should narrow {before} criteria (now {after}).");
    }

    [Fact]
    public async Task Notifications_DependentFieldsFollowTheirSwitch()
    {
        await using var context = await ui.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/administration/notifications");

        var host = page.Locator("input[name='SmtpHost']");
        var mail = page.Locator("input[name='EmailEnabled']");
        if (await mail.IsCheckedAsync())
        {
            await mail.UncheckAsync();
        }

        Assert.True(await host.EvaluateAsync<bool>("el => el.closest('.gated-body').inert"));
        await mail.CheckAsync();
        Assert.False(await host.EvaluateAsync<bool>("el => el.closest('.gated-body').inert"));
        await host.FillAsync("mail.lab.local");
    }

    [Fact]
    public async Task MaintenanceWindows_PreviewFollowsEveryChange_WithoutAButton()
    {
        await using var context = await ui.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/administration/maintenance");

        var picker = page.Locator(".window-picker").First;
        await Assertions.Expect(picker.Locator("button[value='preview']")).ToBeHiddenAsync();
        var before = await picker.Locator(".week .hour.on").CountAsync();

        // Untick every weekday of the first window: fewer hours are covered at once.
        var row = picker.Locator(".window-row").First;
        var checkedDays = row.Locator(".days input:checked");
        while (await checkedDays.CountAsync() > 0)
        {
            await checkedDays.First.UncheckAsync();
        }

        var after = await picker.Locator(".week .hour.on").CountAsync();
        Assert.True(after < before, $"The preview should drop the hours of the first window ({before} -> {after}).");
        Assert.Contains("Not saved yet", await picker.Locator(".window-summary").InnerTextAsync(), StringComparison.Ordinal);

        // Without a weekday the row says so; ticking one hides the hint again.
        await Assertions.Expect(row.Locator(".select-days")).ToBeVisibleAsync();
        await row.Locator(".days label").First.ClickAsync();
        await Assertions.Expect(row.Locator(".select-days")).ToBeHiddenAsync();

        // Weeks of the month are one drop-down; its closed state names the chosen weeks.
        await Assertions.Expect(row.Locator(".weeks input")).ToHaveCountAsync(5);
        var weeks = row.Locator("details.week-choice");
        await Assertions.Expect(weeks.Locator(".week-summary")).ToHaveTextAsync("every week");
        await weeks.Locator("summary").ClickAsync();
        await weeks.Locator("select").SelectOptionAsync("only");
        await weeks.Locator(".weeks label", new LocatorLocatorOptions { HasTextString = "2nd" }).ClickAsync();
        await Assertions.Expect(weeks.Locator(".week-summary")).ToHaveTextAsync("only 2nd week");
        await Assertions.Expect(picker.Locator(".window-summary")).ToContainTextAsync("(2nd week of the month)");
    }

    [Fact]
    public async Task ButtonsAndLinksOnTheSamePage_KeepTheScrollPosition()
    {
        await using var context = await ui.NewContextAsync(1280, 600);
        var page = await context.NewPageAsync();

        // A button that posts the form: "Add window" in the server's maintenance windows (no save).
        await page.GotoAsync($"/servers/{ui.MainServerId}?tab=configuration");
        var inherit = page.Locator(".window-picker input[name$='inherit']").First;
        if (await inherit.IsCheckedAsync())
        {
            await inherit.UncheckAsync();
        }

        var add = page.Locator(".window-picker button", new PageLocatorOptions { HasTextString = "Add window" }).First;
        await add.ScrollIntoViewIfNeededAsync();
        var before = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(before > 200, $"The button should be further down the page ({before}).");
        await add.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        var after = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(Math.Abs(after - before) < 150, $"The page jumped from {before} to {after}.");

        // A sort link only changes the address: the update list keeps its position.
        await page.GotoAsync("/updates");
        var sort = page.Locator("main table thead a.sort").First;
        await sort.ScrollIntoViewIfNeededAsync();
        await page.EvaluateAsync("window.scrollBy(0, 120)");
        var listBefore = await page.EvaluateAsync<double>("window.scrollY");
        await sort.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("sort=", StringComparison.Ordinal));
        var listAfter = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(listBefore > 0 && Math.Abs(listAfter - listBefore) < 150, $"The update list jumped from {listBefore} to {listAfter}.");
    }

    [Fact]
    public async Task LiveRefresh_WaitsWhileAMenuIsOpen_AndKeepsOpenedSections()
    {
        await using var context = await ui.NewContextAsync(1280, 800);
        var page = await context.NewPageAsync();
        var path = $"/servers/{ui.MainServerId}?tab=configuration";

        // The demo agent is idle, so the page gets a two-second live refresh the way a working agent would give it one.
        await page.RouteAsync(url => url.Contains("tab=configuration", StringComparison.Ordinal), async route =>
        {
            var response = await route.FetchAsync();
            var html = (await response.TextAsync()).Replace("<head>", "<head><meta name=\"susyphos-refresh\" content=\"2\" />", StringComparison.Ordinal);
            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = html });
        });
        await page.GotoAsync(path);
        var section = page.Locator("main details.agent-settings").First;
        // Toggled against its default, so only the restore can bring this state back.
        var opened = await section.EvaluateAsync<bool>("d => (d.open = !d.open)");
        await page.Locator("main details[data-menu] > summary").First.ClickAsync();
        await page.EvaluateAsync("window.__beforeRefresh = true");

        await page.WaitForTimeoutAsync(3500);
        Assert.True(await page.EvaluateAsync<bool>("window.__beforeRefresh === true"), "The page reloaded while a menu was open.");

        await page.Keyboard.PressAsync("Escape");
        await page.WaitForFunctionAsync("() => window.__beforeRefresh === undefined", null, new PageWaitForFunctionOptions { Timeout = 5000 });
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        Assert.Equal(opened, await page.Locator("main details.agent-settings").First.EvaluateAsync<bool>("d => d.open"));
    }

    [Fact]
    public async Task PageSize_AppliesOnChange_AndKeepsTheOtherLists()
    {
        await using var context = await ui.NewContextAsync(1280, 900);
        var page = await context.NewPageAsync();

        // The activity list of the main server has more than ten entries; another list keeps its own page meanwhile.
        await page.GotoAsync($"/servers/{ui.MainServerId}?tab=activity&hist.page=2");
        var size = page.Locator("#act-page-size");
        await Assertions.Expect(size).ToHaveValueAsync("10");

        // The script submits the choice at once, so the fallback button is hidden.
        await Assertions.Expect(page.Locator(".page-size:has(#act-page-size) [data-auto-submit-button]")).ToBeHiddenAsync();
        await size.SelectOptionAsync("25");
        await page.WaitForURLAsync(url => url.Contains("act.size=25", StringComparison.Ordinal));
        Assert.Contains("hist.page=2", page.Url, StringComparison.Ordinal);
        await Assertions.Expect(page.Locator("#act-page-size")).ToHaveValueAsync("25");
        await Assertions.Expect(page.Locator(".pager:has(#act-page-size)")).ToContainTextAsync("1–25 of");
    }

    [Fact]
    public async Task AdGroupField_SuggestsGroupsWhileTyping()
    {
        await using var context = await ui.NewContextAsync(1280, 900);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/administration/users");

        var field = page.Locator("input[name='WindowsGroup']");
        await field.PressSequentiallyAsync("susy");
        var suggestions = page.Locator(".suggest button");
        await Assertions.Expect(suggestions).ToHaveCountAsync(2);
        await suggestions.Filter(new LocatorFilterOptions { HasTextString = "Operators" }).ClickAsync();

        await Assertions.Expect(field).ToHaveValueAsync(@"LAB\SUSyphos-Operators");
        await Assertions.Expect(page.Locator(".suggest")).ToBeHiddenAsync();

        // Too short: no list.
        await field.FillAsync(string.Empty);
        await field.PressSequentiallyAsync("s");
        await page.WaitForTimeoutAsync(400);
        await Assertions.Expect(page.Locator(".suggest")).ToBeHiddenAsync();

        // A group Windows filters by User Account Control is listed greyed with the reason and cannot be picked.
        await field.FillAsync(string.Empty);
        await field.PressSequentiallyAsync("domain");
        var filtered = page.Locator(".suggest button", new PageLocatorOptions { HasTextString = "Domain Admins" });
        await Assertions.Expect(filtered).ToBeDisabledAsync();
        await Assertions.Expect(filtered).ToContainTextAsync("won't work (UAC)");
    }

    [Fact]
    public async Task SelectAll_TicksEveryServerShown_AndFollowsSingleRows()
    {
        await using var context = await ui.NewContextAsync(1440, 900);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/servers");

        var all = page.Locator("input[data-select-all]");
        var rows = page.Locator("tbody input[name='Selected']");
        await Assertions.Expect(all).ToBeVisibleAsync();
        await all.CheckAsync();
        Assert.Equal(await rows.CountAsync(), await page.Locator("tbody input[name='Selected']:checked").CountAsync());

        await rows.First.UncheckAsync();
        await Assertions.Expect(all).Not.ToBeCheckedAsync();
        Assert.True(await all.EvaluateAsync<bool>("box => box.indeterminate"));
        await all.CheckAsync();
        Assert.Equal(await rows.CountAsync(), await page.Locator("tbody input[name='Selected']:checked").CountAsync());
        await all.UncheckAsync();
        await Assertions.Expect(page.Locator("tbody input[name='Selected']:checked")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ResultOfAnActionFurtherDown_IsShownInView()
    {
        await using var context = await ui.NewContextAsync(1280, 600);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/administration/users");

        // A refused mapping further down the page: the page keeps its position, the message appears in view.
        var add = page.Locator("button[name='WindowsAction'][value='add']");
        await add.ScrollIntoViewIfNeededAsync();
        Assert.True(await page.EvaluateAsync<double>("window.scrollY") > 100);
        await add.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var toast = page.Locator(".toast");
        await Assertions.Expect(toast).ToBeVisibleAsync();
        await toast.Locator(".toast-close").ClickAsync();
        await Assertions.Expect(toast).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ServerPage_ShowsRunningAndPlannedTasks()
    {
        await using var context = await ui.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"/servers/{ui.MainServerId}");

        var tasks = page.Locator("#tasks");
        await Assertions.Expect(tasks.Locator(".pill", new LocatorLocatorOptions { HasTextString = "Running" })).ToBeVisibleAsync();
        await Assertions.Expect(tasks.Locator("progress")).ToHaveAttributeAsync("value", "42");
        await Assertions.Expect(tasks.Locator(".pill", new LocatorLocatorOptions { HasTextString = "Update search" })).ToBeVisibleAsync();
        await Assertions.Expect(tasks.Locator(".pill", new LocatorLocatorOptions { HasTextString = "Remote action" })).ToBeVisibleAsync();
    }
}
