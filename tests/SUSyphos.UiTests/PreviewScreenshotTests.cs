namespace SUSyphos.UiTests;

/// <summary>
/// Full-page screenshots of the demo lab for looking at a page during development, not a check: skipped unless the
/// environment variable SUSYPHOS_PREVIEW_DIR names a folder. SUSYPHOS_PREVIEW_PAGES lists the pages separated by spaces
/// (as in <see cref="PageLayoutTests.Pages"/>, e.g. "/ /servers/{main}"); SUSYPHOS_PREVIEW_WIDTH sets the window width.
/// </summary>
public sealed class PreviewScreenshotTests(UiFixture ui)
{
    [Fact]
    public async Task Pages_AreWrittenToThePreviewFolder()
    {
        var folder = Environment.GetEnvironmentVariable("SUSYPHOS_PREVIEW_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), "SUSYPHOS_PREVIEW_DIR is not set.");
        var pages = (Environment.GetEnvironmentVariable("SUSYPHOS_PREVIEW_PAGES") ?? "/").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var width = int.TryParse(Environment.GetEnvironmentVariable("SUSYPHOS_PREVIEW_WIDTH"), out var w) ? w : 1440;
        Directory.CreateDirectory(folder!);
        await using var context = await ui.NewContextAsync(width, 900);
        var page = await context.NewPageAsync();
        foreach (var path in pages)
        {
            await page.GotoAsync(await new PageLayoutTests(ui).ResolveAsync(path));
            var name = string.Concat(path.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
            await page.ScreenshotAsync(new() { Path = Path.Combine(folder!, (name.Length == 0 ? "overview" : name) + $"-{width}.png"), FullPage = true });
        }
    }
}
