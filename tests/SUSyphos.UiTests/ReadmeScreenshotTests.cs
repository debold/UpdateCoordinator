using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// Screenshots of the showcase lab for the README (docs/images, 1440 px wide, with the navigation): the overview at window
/// height (1440 × 900, the first impression), the other pages in full length. A normal run
/// checks that every page can still be shown and that the committed image exists; with SUSYPHOS_README_SCREENSHOTS=update
/// (build/Update-HelpScreenshots.ps1 -Readme, at a release) the images are written anew.
/// </summary>
public sealed class ReadmeScreenshotTests(ShowcaseFixture ui)
{
    private const int Width = 1440;

    /// <summary>Image name, page, and for the update page the KB whose rollout it shows (it stands in every ring stage).</summary>
    public static readonly TheoryData<string, string> Shots = new()
    {
        { "overview", "/" },
        { "update-list", "/updates" },
        { "update-progress", "KB5068787" },
        { "server-list", "/servers" },
        { "start-rule", "/administration/release-rules?dialog=rule-new" },
    };

    [Theory]
    [MemberData(nameof(Shots))]
    public async Task Screenshot_CanBeTaken_AndIsCommitted(string name, string path)
    {
        await using var context = await ui.NewContextAsync(Width, 900);
        var page = await context.NewPageAsync();
        if (path.StartsWith('/'))
        {
            await page.GotoAsync(path);
        }
        else
        {
            await page.GotoAsync("/updates");
            await page.Locator($"main a[href^='/updates/']:has-text('{path}')").First.ClickAsync();
        }

        await Assertions.Expect(page.Locator("main h1").First).ToBeVisibleAsync();
        var file = Path.Combine(HelpScreenshotTests.Repository, "docs", "images", name + ".png");
        if (Environment.GetEnvironmentVariable("SUSYPHOS_README_SCREENSHOTS") == "update")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = name != "overview" });
        }

        Assert.True(File.Exists(file), $"{file} is missing; run build/Update-HelpScreenshots.ps1.");
    }
}
