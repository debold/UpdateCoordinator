using System.Net;

using SUSyphos.Server.Help;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class HelpTests
{
    private static readonly string WebRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SUSyphos.Server", "wwwroot");

    [Fact]
    public void Catalog_LoadsEveryHelpPage_InIndexOrder()
    {
        var catalog = new HelpCatalog();

        Assert.NotEmpty(catalog.Topics);
        Assert.Equal("overview", catalog.Topics[0].Slug);
        Assert.All(catalog.Topics, t => Assert.False(string.IsNullOrWhiteSpace(t.Title)));
        Assert.All(catalog.Topics, t => Assert.False(string.IsNullOrWhiteSpace(t.Summary)));
        Assert.Equal(catalog.Topics.Count, catalog.Topics.Select(t => t.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryHelpFile_StartsWithItsTitle()
    {
        // A line before the title once moved a table row of the update list to the top of its help page.
        var topics = Path.Combine(WebRoot, "..", "Help", "Topics");

        Assert.All(Directory.GetFiles(topics, "*.md"), file => Assert.StartsWith("# ", File.ReadAllText(file), StringComparison.Ordinal));

        // UTF-8 read as another code page once turned "×" into "Ã—".
        Assert.All(Directory.GetFiles(topics, "*.md"), file => Assert.DoesNotContain("Ã", File.ReadAllText(file), StringComparison.Ordinal));
    }

    [Fact]
    public void EveryScreenshot_ExistsInTheWebRoot()
    {
        var images = new HelpCatalog().Topics.SelectMany(t => t.Images.Select(i => (t.Slug, Image: i))).ToList();

        Assert.All(images, i => Assert.StartsWith("help-images/", i.Image, StringComparison.Ordinal));
        Assert.All(images, i => Assert.True(File.Exists(Path.Combine(WebRoot, i.Image)), $"{i.Slug}: {i.Image} is missing"));
    }

    [Fact]
    public void EveryLinkBetweenHelpPages_PointsToAnExistingSection()
    {
        var catalog = new HelpCatalog();
        var links = catalog.Topics
            .SelectMany(t => System.Text.RegularExpressions.Regex.Matches(t.Html, "href=\"(help/[^\"]*|#[^\"]*)\"").Select(m => (t.Slug, Href: m.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(links);
        foreach (var (slug, href) in links)
        {
            var target = href.StartsWith('#') ? slug + href : href["help/".Length..];
            var parts = target.Split('#', 2);
            Assert.True(catalog.Contains(parts[0], parts.Length > 1 ? parts[1] : null), $"{slug}: link {href} leads nowhere");
        }
    }

    [Fact]
    public void Headings_TakeExplicitIds_AndRawHtmlIsNotRendered()
    {
        var catalog = new HelpCatalog([new("05-sample", "# Sample {#top}\n\nIntro <script>alert(1)</script> text with `Coverage:Domain`.\n\n## A field {#field}\n\nExplained.\n\n![Shot](help-images/x.png)")]);

        var topic = Assert.Single(catalog.Topics);
        Assert.Equal("sample", topic.Slug);
        Assert.Equal(5, topic.Order);
        Assert.Equal("Sample", topic.Title);
        Assert.EndsWith("text with Coverage:Domain.", topic.Summary, StringComparison.Ordinal);
        Assert.Contains("field", (IReadOnlySet<string>)topic.Anchors);
        Assert.Equal(["help-images/x.png"], topic.Images);
        Assert.DoesNotContain("<script>", topic.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", topic.Html, StringComparison.Ordinal);
        Assert.True(catalog.Contains("sample", "field"));
        Assert.True(catalog.Contains("sample", null));
        Assert.False(catalog.Contains("sample", "missing"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Overview")]
    [InlineData("../secret")]
    [InlineData("over view")]
    [InlineData("missing")]
    public void Find_ReturnsNothing_ForUnknownOrMalformedNames(string slug) =>
        Assert.Null(new HelpCatalog([new("10-overview", "# Overview\n\nText.")]).Find(slug == "Overview" ? "overview/" : slug));

    [Fact]
    public void InvalidFileName_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() => new HelpCatalog([new("10-Bad Name", "# Bad")]));

    [Theory]
    [InlineData("/help")]
    [InlineData("/help/overview")]
    [InlineData("/help/server-detail")]
    public async Task HelpPages_AreOpenWithoutSignIn(string path)
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);

        using var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("help-", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void Search_FindsTheSection_WithTheWordMarked()
    {
        var catalog = new HelpCatalog([
            new("10-overview", "# Overview\n\nIntro text.\n\n## Leases {#leases}\n\nA lease lets one server of a group restart."),
            new("20-servers", "# Servers\n\nThe list of servers."),
        ]);

        var hit = Assert.Single(catalog.Search("lease restart"));

        Assert.Equal("overview", hit.Topic.Slug);
        Assert.Equal("leases", hit.Anchor);
        Assert.Equal("lease", hit.Match, ignoreCase: true);
    }

    [Fact]
    public void LinksWithinAPage_PointToThatPage()
    {
        var topic = new HelpCatalog([new("10-overview", "# Overview\n\nSee [leases](#leases) and [servers](servers#list).\n\n## Leases {#leases}\n\nText.")]).Find("overview")!;

        Assert.Contains("href=\"help/overview#leases\"", topic.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"servers#list\"", topic.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData("nothing-like-this")]
    public void Search_ReturnsNothing_ForEmptyTooShortOrUnknownQueries(string? query) =>
        Assert.Empty(new HelpCatalog([new("10-overview", "# Overview\n\nText about servers.")]).Search(query));

    [Fact]
    public void Areas_PlaceEveryPageExactlyOnce()
    {
        var catalog = new HelpCatalog();

        var placed = catalog.Areas.SelectMany(a => a.Topics).ToList();

        Assert.Equal(catalog.Topics.Count, placed.Count);
        Assert.Equal(catalog.Topics.Select(t => t.Slug).Order(), placed.Select(t => t.Slug).Order());
        Assert.All(catalog.Topics, t => Assert.NotNull(catalog.AreaOf(t)));
    }

    [Fact]
    public async Task SearchPage_MarksTheHits_AndATopicHasOnThisPage()
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);

        var search = await (await browser.GetAsync("/help?q=lease")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("help-hits", search, StringComparison.Ordinal);
        Assert.Contains("<mark>", search, StringComparison.Ordinal);

        var none = await (await browser.GetAsync("/help?q=%3Cscript%3Ezzqq")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Nothing found", none, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>zzqq", none, StringComparison.Ordinal);

        var topic = await (await browser.GetAsync("/help/server-detail")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("help-toc", topic, StringComparison.Ordinal);
        Assert.Contains("data-section=", topic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/help/missing")]
    [InlineData("/help/Over_view")]
    public async Task UnknownHelpPage_AnswersNotFound(string path)
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);

        using var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("help-article", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
