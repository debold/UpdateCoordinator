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
public sealed record HelpTopic(string Slug, string Title, string Summary, int Order, string Html, FrozenSet<string> Anchors, IReadOnlyList<string> Images, IReadOnlyList<(string Id, string Title)> Sections)
{
    /// <summary>The plain text of each part of the page (before the first section: the page itself), for the search.</summary>
    public IReadOnlyList<HelpPassage> Passages { get; init; } = [];
}

/// <summary>A part of a help page as plain text: the heading it belongs to (null for the start of the page) and its text.</summary>
public sealed record HelpPassage(string? Anchor, string Heading, string Text);

/// <summary>An area of the help (Marc, 2026-11-13, variant C): a few topics that belong together, with one sentence.</summary>
public sealed record HelpArea(string Name, string Description, IReadOnlyList<HelpTopic> Topics);

/// <summary>A search result: the page, the part and the words around the first hit, split so the hit can be marked.</summary>
public sealed record HelpHit(HelpTopic Topic, string? Anchor, string Heading, string Before, string Match, string After, int Score);

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
        var placed = AreaDefinitions.SelectMany(a => a.Slugs).ToHashSet(StringComparer.Ordinal);
        Areas = [.. AreaDefinitions.Select(a => new HelpArea(a.Name, a.Description, [.. a.Slugs.Length == 0
                ? Topics.Where(t => !placed.Contains(t.Slug))
                : a.Slugs.Select(s => _topics.GetValueOrDefault(s)).OfType<HelpTopic>()]))
            .Where(a => a.Topics.Count > 0)];
    }

    /// <summary>All pages in index order.</summary>
    public IReadOnlyList<HelpTopic> Topics { get; }

    /// <summary>The areas of the help in order; every page is in exactly one (unknown pages go to Administration).</summary>
    public IReadOnlyList<HelpArea> Areas { get; }

    private static readonly (string Name, string Description, string[] Slugs)[] AreaDefinitions =
    [
        ("Getting started", "What SUSyphos does, how the pages are built and how lists work.", ["overview", "concepts", "tables"]),
        ("Servers", "The server list, a server's tabs, approvals, groups and how servers get their ring.", ["servers", "server-detail", "approvals", "coverage", "groups", "group-detail", "assignment-rules"]),
        ("Updates & software", "Updates, software packages and the security state of the Windows versions.", ["updates", "update-detail", "software", "package-detail", "operating-systems"]),
        ("Rollout & rules", "How updates enter the rings and move on from ring to ring.", ["rings", "release-rules"]),
        ("Windows & security", "Maintenance windows, reboots, software windows and the certificate.", ["maintenance-windows", "certificate"]),
        ("Administration", "Accounts, notifications, backup, the audit log, agent settings and the central server.", []),
    ];

    /// <summary>The area of a page.</summary>
    public HelpArea AreaOf(HelpTopic topic) => Areas.First(a => a.Topics.Contains(topic));

    /// <summary>
    /// Full-text search over every page (Marc, 2026-11-13): every word of the query must appear in a part of a page; hits
    /// in the title count most, then in the heading, then in the text. At most <paramref name="limit"/> hits, best first.
    /// </summary>
    public IReadOnlyList<HelpHit> Search(string? query, int limit = 30)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();
        if (words.Count == 0 || words.Sum(w => w.Length) < 2)
        {
            return [];
        }

        var hits = new List<HelpHit>();
        foreach (var topic in Topics)
        {
            foreach (var passage in topic.Passages)
            {
                var all = $"{topic.Title} {passage.Heading} {passage.Text}";
                if (!words.All(w => all.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var score = words.Sum(w => (topic.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ? 10 : 0)
                    + (passage.Heading.Contains(w, StringComparison.OrdinalIgnoreCase) ? 5 : 0)
                    + Math.Min(5, Occurrences(passage.Text, w)));
                var first = words.Select(w => (Word: w, Index: passage.Text.IndexOf(w, StringComparison.OrdinalIgnoreCase))).Where(x => x.Index >= 0).OrderBy(x => x.Index).FirstOrDefault();
                var (before, match, after) = first.Word is null
                    ? (string.Empty, string.Empty, Shorten(passage.Text, 0, 200))
                    : (Prefix(passage.Text, first.Index, 90), passage.Text.Substring(first.Index, first.Word.Length), Shorten(passage.Text, first.Index + first.Word.Length, 140));
                hits.Add(new HelpHit(topic, passage.Anchor, passage.Heading, before, match, after, score));
            }
        }

        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Topic.Order).Take(limit)];
    }

    private static int Occurrences(string text, string word)
    {
        var count = 0;
        for (var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static string Prefix(string text, int end, int length) =>
        end <= length ? text[..end] : "…" + text[(end - length)..end].TrimStart();

    private static string Shorten(string text, int start, int length) =>
        text.Length - start <= length ? text[start..] : text.Substring(start, length).TrimEnd() + "…";

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

        // The dashboard sets <base href="/">, so a bare "#section" would lead to the overview: point it to this page.
        foreach (var link in document.Descendants<LinkInline>().Where(l => !l.IsImage && l.Url is ['#', ..]))
        {
            link.Url = $"help/{slug}{link.Url}";
        }

        var sections = headings.Where(h => h.Level == 2 && h.GetAttributes().Id is not null).Select(h => (h.GetAttributes().Id!, PlainText(h.Inline))).ToList();
        return new HelpTopic(slug, title, summary, order, document.ToHtml(Pipeline), anchors, images, sections) { Passages = Passages(document, title) };
    }

    /// <summary>The text of the page by section: everything from one heading with an id to the next.</summary>
    private static List<HelpPassage> Passages(MarkdownDocument document, string title)
    {
        var passages = new List<HelpPassage>();
        string? anchor = null;
        var heading = title;
        var text = new System.Text.StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                passages.Add(new HelpPassage(anchor, heading, text.ToString().Trim()));
                text.Clear();
            }
        }

        foreach (var block in document.Descendants<LeafBlock>())
        {
            if (block is HeadingBlock h)
            {
                if (h.Level >= 2 && h.GetAttributes().Id is { } id)
                {
                    Flush();
                    anchor = id;
                    heading = PlainText(h.Inline);
                }

                continue;
            }

            if (block.Inline is { } inline)
            {
                text.Append(PlainText(inline)).Append(' ');
            }
        }

        Flush();
        return passages;
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
