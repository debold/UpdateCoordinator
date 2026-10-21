using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// Screenshots for the help pages of dialogs and controls that are not visible on the page itself (modal dialogs, filter
/// and multi-select drop-downs), taken from the demo lab. A normal run checks that every screenshot can still be taken and
/// that the committed image exists. With the environment variable SUSYPHOS_HELP_SCREENSHOTS=update the images under
/// src/SUSyphos.Server/wwwroot/help-images are written anew (build/Update-HelpScreenshots.ps1); commit them afterwards.
/// </summary>
public sealed class HelpScreenshotTests(UiFixture ui)
{
    /// <summary>Image name, page, optional element to click first, and the element the screenshot shows.</summary>
    public static readonly TheoryData<string, string, string?, string> Shots = new()
    {
        { "tables-filter", "/servers", "summary[aria-label='Filter Status']", "details.col-filter[open]" },
        { "server-restart-dialog", "/servers/{main}?dialog=restart", null, "[role='dialog']" },
        { "server-remove-dialog", "/servers/{main}?dialog=remove", null, "[role='dialog']" },
        { "server-groups-dropdown", "/servers/{main}", "details.multi > summary", ".field:has(details.multi[open])" },
        { "ring-add-servers", "/rings/1?dialog=add", null, "[role='dialog']" },
        { "users-new-dialog", "/administration/users?dialog=new", null, "[role='dialog']" },
        { "groups-dialog", "/administration/groups", "main a[href*='dialog=edit']", "[role='dialog']" },
        { "assignment-rule-dialog", "/administration/rules?dialog=new", null, "[role='dialog']" },
        { "release-rules-ring-dialog", "/administration/release-rules", "main a[href*='dialog=ring&']", "[role='dialog']" },
        { "release-rules-start-dialog", "/administration/release-rules?dialog=rule-new", null, "[role='dialog']" },
    };

    /// <summary>Viewport rectangle (left, top, right, bottom) of an element and all its visible descendants.</summary>
    private const string UnionScript = """
        (element) => {
            let [left, top, right, bottom] = [Infinity, Infinity, -Infinity, -Infinity];
            for (const el of [element, ...element.querySelectorAll('*')]) {
                const r = el.getBoundingClientRect();
                if (r.width === 0 || r.height === 0 || !el.checkVisibility()) continue;
                left = Math.min(left, r.left); top = Math.min(top, r.top); right = Math.max(right, r.right); bottom = Math.max(bottom, r.bottom);
            }
            return [left, top, Math.min(right, window.innerWidth), Math.min(bottom, window.innerHeight)];
        }
        """;

    private static readonly bool Update = Environment.GetEnvironmentVariable("SUSYPHOS_HELP_SCREENSHOTS") == "update";

    [Theory]
    [MemberData(nameof(Shots))]
    public async Task Screenshot_CanBeTaken_AndIsCommitted(string name, string path, string? click, string element)
    {
        await using var context = await ui.NewContextAsync(1280, 900);
        var page = await context.NewPageAsync();
        await page.GotoAsync(await new PageLayoutTests(ui).ResolveAsync(path));
        if (click is not null)
        {
            await page.Locator(click).First.ClickAsync();
        }

        var target = page.Locator(element).First;
        await Assertions.Expect(target).ToBeVisibleAsync();
        var file = Path.Combine(ImageFolder, name + ".png");
        if (Update)
        {
            // The element with everything that pops out of it (drop-downs are positioned outside their box), plus a
            // little room for edges and shadows; dialogs bring their own shadow.
            await target.ScrollIntoViewIfNeededAsync();
            var box = await target.EvaluateAsync<double[]>(UnionScript);
            var margin = element.Contains("dialog", StringComparison.Ordinal) ? 0 : 8;
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = file,
                Clip = new Clip
                {
                    X = (float)Math.Max(0, box[0] - margin),
                    Y = (float)Math.Max(0, box[1] - margin),
                    Width = (float)(box[2] - box[0] + (2 * margin)),
                    Height = (float)(box[3] - box[1] + (2 * margin)),
                },
            });
        }

        Assert.True(File.Exists(file), $"{file} is missing; run build/Update-HelpScreenshots.ps1.");
    }

    /// <summary>Every image a help page shows has an entry here, so it is renewed with the others.</summary>
    [Fact]
    public void EveryHelpImage_HasAScreenshotDefinition()
    {
        var defined = Shots.Select(s => (string)s.Data.Item1 + ".png").ToHashSet(StringComparer.Ordinal);
        var used = Directory.GetFiles(Path.Combine(Repository, "src", "SUSyphos.Server", "Help", "Topics"), "*.md")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"\(help-images/([a-z0-9-]+\.png)\)").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(used.Except(defined));
        Assert.Empty(defined.Except(used));
    }

    private static string ImageFolder => Path.Combine(Repository, "src", "SUSyphos.Server", "wwwroot", "help-images");

    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SUSyphos.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}
