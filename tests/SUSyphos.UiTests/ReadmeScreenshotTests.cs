using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// Screenshots of the showcase lab for the README (docs/images, 1440 px wide; README variant A "Showcase", Marc
/// 2026-11-16): the overview at window height (1440 × 900, the first impression), the pages of the feature blocks at
/// window height or as one box, so they stay readable in the narrow table column. A normal run checks that every page can
/// still be shown and that the committed image exists; with SUSYPHOS_README_SCREENSHOTS=update
/// (build/Update-HelpScreenshots.ps1 -Readme, a step of every release) the images are written anew.
/// </summary>
[Collection(ShowcaseLab.Name)]
public sealed class ReadmeScreenshotTests(ShowcaseFixture ui)
{
    private const int Width = 1440;

    /// <summary>Image name, page (or the KB whose update page it shows), and the element for a box shot (null: the window).</summary>
    public static readonly TheoryData<string, string, string?> Shots = new()
    {
        { "overview", "/", null },
        { "update-progress", "KB5068787", "main .grid-side" },
        { "software", "/software", null },
        { "maintenance-windows", "/administration/maintenance?windows=software", null },
        { "server-security", "/servers/{main}?tab=security", "main section.box#security" },
    };

    [Theory]
    [MemberData(nameof(Shots))]
    public async Task Screenshot_CanBeTaken_AndIsCommitted(string name, string path, string? element)
    {
        await using var context = await ui.NewContextAsync(Width, 900);
        var page = await context.NewPageAsync();
        if (path.StartsWith('/'))
        {
            await page.GotoAsync(await new PageLayoutTests(ui).ResolveAsync(path));
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
            if (element is null)
            {
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = file });
            }
            else
            {
                await page.Locator(element).First.ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            }
        }

        Assert.True(File.Exists(file), $"{file} is missing; run build/Update-HelpScreenshots.ps1 -Readme.");
    }

    /// <summary>Every image the README shows has a screenshot definition here, and no unused image stays in docs/images.</summary>
    [Fact]
    public void ReadmeImages_AreExactlyTheScreenshots()
    {
        var readme = File.ReadAllText(Path.Combine(HelpScreenshotTests.Repository, "README.md"));
        var used = Regex.Matches(readme, @"docs/images/([a-z0-9-]+)\.png").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var defined = Shots.Select(s => (string)s.Data.Item1).ToHashSet(StringComparer.Ordinal);
        var present = Directory.GetFiles(Path.Combine(HelpScreenshotTests.Repository, "docs", "images"), "*.png").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(used.Except(defined));
        Assert.Empty(defined.Except(used));
        Assert.Empty(present.Except(used!));
    }

    /// <summary>
    /// The README announces the newest released version (Marc, 2026-11-16: the README is a step of every release): the
    /// version in the NOTE box and its link must be the newest release in CHANGELOG.md.
    /// </summary>
    [Fact]
    public void ReadmeNote_NamesTheNewestRelease()
    {
        var changelog = File.ReadAllText(Path.Combine(HelpScreenshotTests.Repository, "CHANGELOG.md"));
        var newest = Regex.Match(changelog, @"^## \[(\d+\.\d+\.\d+(?:-[a-z]+\.\d+)?)\]", RegexOptions.Multiline).Groups[1].Value;
        var readme = File.ReadAllText(Path.Combine(HelpScreenshotTests.Repository, "README.md"));
        var note = Regex.Match(readme, @"\[SUSyphos (?<version>[^\]]+)\]\(https://github\.com/debold/UpdateCoordinator/releases/tag/release/v(?<tag>[^)]+)\)");

        Assert.False(string.IsNullOrEmpty(newest), "CHANGELOG.md names no release.");
        Assert.True(note.Success, "The README has no NOTE box naming a release.");
        Assert.Equal(newest, note.Groups["version"].Value);
        Assert.Equal(newest, note.Groups["tag"].Value);
    }
}
