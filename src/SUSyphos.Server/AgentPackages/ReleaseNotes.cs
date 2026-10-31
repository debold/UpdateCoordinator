using System.Text.Json;

using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.AgentPackages;

/// <summary>
/// The notes of the GitHub releases, shown under "What's new" for agent versions and the central server update (Marc,
/// 2026-10-31). The check stores them in the settings table (no schema change); they are Markdown written by the project
/// and shown without HTML, images or links other than https, so a manipulated release text cannot run anything.
/// </summary>
internal static class ReleaseNotes
{
    public const string Setting = "Releases.Notes";

    /// <summary>Longest note kept per release; longer ones are cut and point to the release page.</summary>
    public const int MaxLength = 20_000;

    /// <summary>Releases whose notes are kept (the check reads the newest 30).</summary>
    public const int MaxReleases = 30;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();

    /// <summary>The note as kept: trimmed, cut to <see cref="MaxLength"/>, null when empty.</summary>
    public static string? Limit(string? notes)
    {
        var text = notes?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > MaxLength ? text[..MaxLength] + "\n\n…" : text;
    }

    /// <summary>Stores the notes of the releases by version, replacing the notes kept so far.</summary>
    public static async Task SaveAsync(ServerDbContext db, IEnumerable<AgentRelease> releases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(releases);
        var notes = releases.Where(r => r.Notes is not null).OrderByDescending(r => r.Version).Take(MaxReleases)
            .GroupBy(r => r.Version.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Notes!, StringComparer.Ordinal);
        var value = JsonSerializer.Serialize(notes);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == Setting, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = Setting, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The notes kept by version; empty when the check has not read any.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == Setting).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return value is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// The note as HTML: Markdown without raw HTML; images become their text, links other than https lose their target,
    /// and https links open in a new tab without passing the dashboard on.
    /// </summary>
    public static string ToHtml(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage || !Uri.TryCreate(link.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                // Keep the text, drop the target.
                var text = new LiteralInline(string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString())));
                link.ReplaceBy(text);
                continue;
            }

            var attributes = link.GetAttributes();
            attributes.AddPropertyIfNotExist("target", "_blank");
            attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().Where(a => a.IsEmail || !a.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }

        return document.ToHtml(Pipeline);
    }
}
