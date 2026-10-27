using System.Collections.Frozen;
using System.Text.RegularExpressions;

using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SUSyphos.Server.Help;

/// <summary>One help page: rendered once from its Markdown source.</summary>
/// <param name="Slug">Address part after <c>/help/</c>: the file name of the source without its order prefix.</param>
/// <param name="Title">Text of the first level-1 heading.</param>
/// <param name="Summary">First paragraph, shown in the index.</param>
/// <param name="Order">Position in the index: the number the file name starts with (<c>20-servers.md</c>).</param>
/// <param name="Html">The rendered page.</param>
/// <param name="Anchors">Element ids of the headings, the targets of the help links in the dashboard.</param>
/// <param name="Images">Image paths the page shows (screenshots under <c>help-images/</c>).</param>
/// <param name="Sections">The level-2 headings with their ids, for the navigation next to the page.</param>
public sealed record HelpTopic(string Slug, string Title, string Summary, int Order, string Html, FrozenSet<string> Anchors, IReadOnlyList<string> Images, IReadOnlyList<(string Id, string Title)> Sections);

/// <summary>
/// The help pages of the dashboard. Sources are Markdown files compiled into the assembly (<c>Help/Topics</c>); raw HTML
/// in them is not rendered, so a page can only contain what Markdown expresses. Headings take explicit ids
/// (<c>## Column filter {#filter}</c>), which the help links of the dashboard point to.
/// </summary>
public sealed partial class HelpCatalog
{
    private const string ResourcePrefix = "Help.";
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseGenericAttributes()
        .UsePipeTables()
        .UseAutoIdentifiers()
        .DisableHtml()
        .Build();

    private readonly FrozenDictionary<string, HelpTopic> _topics;

    public HelpCatalog()
        : this(LoadSources())
    {
    }

    /// <summary>Builds the catalog from sources given as file name (without .md) and Markdown text (tests).</summary>
    public HelpCatalog(IEnumerable<KeyValuePair<string, string>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _topics = sources.Select(s => Parse(s.Key, s.Value)).ToFrozenDictionary(t => t.Slug, StringComparer.OrdinalIgnoreCase);
        Topics = [.. _topics.Values.OrderBy(t => t.Order).ThenBy(t => t.Title, StringComparer.Ordinal)];
    }

    /// <summary>All pages in index order.</summary>
    public IReadOnlyList<HelpTopic> Topics { get; }

    /// <summary>The page for a slug, or null; slugs are letters, digits and hyphens only.</summary>
    public HelpTopic? Find(string? slug) =>
        slug is not null && SlugPattern().IsMatch(slug) && _topics.TryGetValue(slug, out var topic) ? topic : null;

    /// <summary>True when the page exists and has a heading with this id (an empty anchor means the page itself).</summary>
    public bool Contains(string slug, string? anchor) =>
        Find(slug) is { } topic && (string.IsNullOrEmpty(anchor) || topic.Anchors.Contains(anchor));

    private static IEnumerable<KeyValuePair<string, string>> LoadSources()
    {
        var assembly = typeof(HelpCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return new(name[ResourcePrefix.Length..^3], reader.ReadToEnd());
        }
    }

    private static HelpTopic Parse(string name, string markdown)
    {
        var prefix = OrderPrefix().Match(name);
        var order = prefix.Success ? int.Parse(prefix.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
        var slug = prefix.Success ? name[prefix.Length..] : name;
        if (!SlugPattern().IsMatch(slug))
        {
            throw new InvalidOperationException($"Invalid help page name '{slug}'.");
        }

        var document = Markdown.Parse(markdown, Pipeline);
        var headings = document.Descendants<HeadingBlock>().ToList();
        var title = headings.FirstOrDefault(h => h.Level == 1) is { } h1 ? PlainText(h1.Inline) : slug;
        var summary = document.Descendants<ParagraphBlock>().FirstOrDefault() is { } first ? PlainText(first.Inline) : string.Empty;
        var anchors = headings.Select(h => h.GetAttributes().Id).OfType<string>().ToFrozenSet(StringComparer.Ordinal);
        var images = document.Descendants<LinkInline>().Where(l => l.IsImage && l.Url is not null).Select(l => l.Url!).ToList();
        var sections = headings.Where(h => h.Level == 2 && h.GetAttributes().Id is not null).Select(h => (h.GetAttributes().Id!, PlainText(h.Inline))).ToList();
        return new HelpTopic(slug, title, summary, order, document.ToHtml(Pipeline), anchors, images, sections);
    }

    private static string PlainText(ContainerInline? inline) =>
        inline is null
            ? string.Empty
            : string.Concat(inline.Descendants<Inline>().Select(i => i switch
            {
                LiteralInline literal => literal.Content.ToString(),
                CodeInline code => code.Content,
                LineBreakInline => " ",
                _ => string.Empty,
            })).Trim();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.None, 1000)]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^(\d{1,4})-", RegexOptions.None, 1000)]
    private static partial Regex OrderPrefix();
}
