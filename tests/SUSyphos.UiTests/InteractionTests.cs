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
    [InlineData("/rings/1")]
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

        // Weeks of the month are offered for every window.
        await Assertions.Expect(row.Locator(".weeks input")).ToHaveCountAsync(5);
    }

    [Fact]
    public async Task ButtonsAndLinksOnTheSamePage_KeepTheScrollPosition()
    {
        await using var context = await ui.NewContextAsync(1280, 600);
        var page = await context.NewPageAsync();

        // A button that posts the form: "Add window" in the server's maintenance windows (no save).
        await page.GotoAsync($"/servers/{ui.MainServerId}");
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

        // A link that only changes the address: an update in the ring's list opens its status per server.
        await page.GotoAsync("/rings/1");
        var link = page.Locator(".grid-side table a").Nth(1);
        await link.ScrollIntoViewIfNeededAsync();
        await page.EvaluateAsync("window.scrollBy(0, 120)");
        var ringBefore = await page.EvaluateAsync<double>("window.scrollY");
        await link.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("tab=update", StringComparison.Ordinal));
        var ringAfter = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(ringBefore > 0 && Math.Abs(ringAfter - ringBefore) < 150, $"The ring page jumped from {ringBefore} to {ringAfter}.");
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
