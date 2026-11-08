using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// The ring boxes of the update list show the stage at a glance (Marc, 2026-11-08, variant A): a released ring is filled in
/// its colour, a ring not reached yet is dashed, and done or failed rings carry a badge.
/// </summary>
public sealed class RingStageTests(UiFixture ui)
{
    [Fact]
    public async Task ReleasedRings_AreFilled_AndWaitingRingsDashed()
    {
        await using var context = await ui.NewContextAsync(1440, 900);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/updates");

        var released = page.Locator("main .ring-stage.pend, main .ring-stage.ok, main .ring-stage.fail").First;
        await Assertions.Expect(released).ToBeVisibleAsync();
        Assert.True(
            await released.EvaluateAsync<bool>("e => { const s = getComputedStyle(e); return s.backgroundColor === s.borderTopColor && s.color === 'rgb(255, 255, 255)'; }"),
            "a released ring is not filled in its colour");

        var waiting = page.Locator("main .ring-stage.neutral").First;
        await Assertions.Expect(waiting).ToBeVisibleAsync();
        Assert.Equal("dashed", await waiting.EvaluateAsync<string>("e => getComputedStyle(e).borderTopStyle"));
        Assert.Equal("none", await waiting.EvaluateAsync<string>("e => getComputedStyle(e, '::after').content"));
    }

    [Fact]
    public async Task DoneAndFailedRings_CarryABadge()
    {
        await using var context = await ui.NewContextAsync(1440, 900);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/updates?view=all");

        var badges = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('main .ring-stage.ok, main .ring-stage.fail')].map(e => getComputedStyle(e, '::after').content)");
        Assert.NotEmpty(badges);
        Assert.All(badges, badge => Assert.NotEqual("none", badge));
    }
}
