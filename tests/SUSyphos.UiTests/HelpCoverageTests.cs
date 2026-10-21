using System.Text.Json;

using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// The integrated help is complete: every page has a help link, every visible field, switch and button resolves to a help
/// section, that section exists and mentions the control by its label, and every help link on the page points to an
/// existing section. A control is explained by the nearest help link in its scope (its label, field, table tools, action
/// band, box, dialog or page), or explicitly by <c>data-help</c> on itself or an ancestor; <c>data-help-term</c> names
/// the word the section must contain when the visible label carries data (e.g. a server name).
/// </summary>
public sealed class HelpCoverageTests(UiFixture ui)
{
    private static readonly string[] Extra =
    [
        "/help",
        "/help/overview",
        "/login",
    ];

    public static readonly TheoryData<string> Pages = [.. PageLayoutTests.Pages.Select(p => p.Data), .. Extra];

    private const string CollectScript = """
        () => {
            const scopes = 'label, .field, .help-scope, .table-tools, .band, th, .box, .dialog, main';
            const visible = (el) => {
                const r = el.getBoundingClientRect();
                return r.width > 0 && r.height > 0 && el.checkVisibility({ visibilityProperty: true, contentVisibilityAuto: true });
            };
            const links = [...document.querySelectorAll('a.help-link')];
            const owner = (link) => link.parentElement.closest(scopes);
            const clean = (text) => (text || '').replace(/\s+/g, ' ').replace(/[:*]+$/, '').trim();
            const labelText = (control) => {
                const label = control.labels && control.labels[0];
                if (!label) return '';
                const span = label.classList.contains('field') ? label.querySelector(':scope > span') : null;
                const copy = (span || label).cloneNode(true);
                copy.querySelectorAll('input, select, textarea, .help-link, .note, .hint, small').forEach(e => e.remove());
                return clean(copy.textContent);
            };
            const fieldLabel = (control) => {
                const label = control.closest('.field') && control.closest('.field').querySelector('.field-label');
                if (!label) return '';
                const copy = label.cloneNode(true);
                copy.querySelectorAll('.help-link').forEach(e => e.remove());
                return clean(copy.textContent);
            };
            const term = (control) => (control.closest('[data-help-term]') && control.closest('[data-help-term]').dataset.helpTerm)
                || labelText(control)
                || (control.tagName === 'BUTTON' ? clean(control.innerText) : '')
                || fieldLabel(control)
                || clean(control.getAttribute('aria-label'))
                || clean(control.innerText)
                || clean(control.value)
                || clean(control.getAttribute('title'))
                || clean(control.getAttribute('placeholder'));
            const resolve = (control) => {
                const explicit = control.closest('[data-help]');
                if (explicit) return 'help/' + explicit.dataset.help;
                for (let scope = control.closest(scopes); scope; scope = scope.parentElement && scope.parentElement.closest(scopes)) {
                    const own = links.find(l => owner(l) === scope);
                    if (own) return own.getAttribute('href');
                }
                return null;
            };
            const controls = [...document.querySelectorAll('input:not([type=hidden]), select, textarea, button, summary')]
                .filter(c => !c.closest('nav.nav') && visible(c));
            return {
                pageLink: !!document.querySelector('h1 a.help-link'),
                links: links.map(l => l.getAttribute('href')),
                controls: controls.map(c => ({ term: term(c), href: resolve(c), html: c.outerHTML.slice(0, 120) })),
            };
        }
        """;

    private const string SectionsScript = """
        () => {
            const article = document.querySelector('.help-article');
            if (!article) return null;
            const sections = [{ id: '', text: article.innerText }];
            for (const heading of article.querySelectorAll('h1[id], h2[id], h3[id]')) {
                const level = +heading.tagName[1];
                let text = heading.innerText;
                for (let next = heading.nextElementSibling; next; next = next.nextElementSibling) {
                    if (/^H[1-6]$/.test(next.tagName) && +next.tagName[1] <= level) break;
                    text += '\n' + next.innerText + ' ' + [...next.querySelectorAll('img[alt]')].map(i => i.alt).join(' ');
                }
                sections.push({ id: heading.id, text: level === 1 ? article.innerText : text });
            }
            return sections;
        }
        """;

    private static readonly Dictionary<string, Dictionary<string, string>?> SectionCache = [];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryControl_IsExplainedInTheHelp(string path)
    {
        // The sign-in page is checked as a visitor without session; every other page as administrator.
        await using var context = path == "/login"
            ? await ui.Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, BaseURL = ui.BaseAddress.ToString() })
            : await ui.NewContextAsync(1440, 900);
        var page = await context.NewPageAsync();
        var start = await new PageLayoutTests(ui).ResolveAsync(path);
        await page.GotoAsync(start);
        var failures = new List<string>();
        await CheckAsync(context, page, path, failures);

        // Every dialog the page links to (edit, delete, add and so on) is checked too, also those that need an id.
        if (!path.Contains("dialog=", StringComparison.Ordinal))
        {
            var dialogs = await page.EvalOnSelectorAllAsync<string[]>("main a[href*='dialog=']", "links => [...new Set(links.map(a => a.getAttribute('href')))]");
            foreach (var dialog in dialogs.DistinctBy(d => System.Text.RegularExpressions.Regex.Match(d, "dialog=([a-z-]+)").Value))
            {
                await page.GotoAsync(new Uri(new Uri(page.Url), dialog).ToString());
                var before = failures.Count;
                await CheckAsync(context, page, dialog, failures);
                if (await page.Locator("[role='dialog']").CountAsync() == 0)
                {
                    failures.Add($"{dialog}: no dialog opened");
                }

                for (var i = before; i < failures.Count; i++)
                {
                    failures[i] = $"[{dialog}] {failures[i]}";
                }
            }
        }

        Assert.True(failures.Count == 0, $"{path}\n  " + string.Join("\n  ", failures.Distinct()));
    }

    private async Task CheckAsync(IBrowserContext context, IPage page, string path, List<string> failures)
    {
        var result = await page.EvaluateAsync<JsonElement>(CollectScript);
        if (!path.StartsWith("/help", StringComparison.Ordinal) && !result.GetProperty("pageLink").GetBoolean())
        {
            failures.Add("the page title has no help link");
        }

        foreach (var link in result.GetProperty("links").EnumerateArray().Select(l => l.GetString()!).Distinct())
        {
            if (await SectionAsync(context, link) is null)
            {
                failures.Add($"help link {link} points to a missing page or section");
            }
        }

        if (!path.StartsWith("/help", StringComparison.Ordinal))
        {
            foreach (var control in result.GetProperty("controls").EnumerateArray())
            {
                var term = control.GetProperty("term").GetString() ?? string.Empty;
                var href = control.GetProperty("href").GetString();
                var html = control.GetProperty("html").GetString();
                if (href is null)
                {
                    failures.Add($"no help for \"{term}\": {html}");
                    continue;
                }

                var section = await SectionAsync(context, href);
                if (section is null)
                {
                    failures.Add($"\"{term}\" links to {href}, which does not exist");
                }
                else if (term.Length == 0)
                {
                    failures.Add($"control without a label: {html}");
                }
                else if (!Normalize(section).Contains(Normalize(term), StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{href} does not mention \"{term}\"");
                }
            }
        }
    }

    /// <summary>A heading id and the text of its section, as the browser returns it.</summary>
    private sealed class Section
    {
        public string Id { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace('’', '\'');

    /// <summary>Text of the help section a link points to, or null when page or section is missing.</summary>
    private async Task<string?> SectionAsync(IBrowserContext context, string href)
    {
        var hash = href.IndexOf('#', StringComparison.Ordinal);
        var topic = hash < 0 ? href : href[..hash];
        var anchor = hash < 0 ? string.Empty : href[(hash + 1)..];
        Dictionary<string, string>? sections;
        lock (SectionCache)
        {
            SectionCache.TryGetValue(topic, out sections);
        }

        if (sections is null)
        {
            var page = await context.NewPageAsync();
            try
            {
                var response = await page.GotoAsync(new Uri(ui.BaseAddress, topic).ToString());
                var found = response is { Ok: true } ? await page.EvaluateAsync<Section[]?>(SectionsScript) : null;
                sections = found?.ToDictionary(f => f.Id, f => f.Text, StringComparer.Ordinal);
            }
            catch (PlaywrightException)
            {
                sections = null;
            }
            finally
            {
                await page.CloseAsync();
            }
            if (sections is not null)
            {
                lock (SectionCache)
                {
                    SectionCache[topic] = sections;
                }
            }
        }

        return sections is not null && sections.TryGetValue(anchor, out var text) ? text : null;
    }
}
