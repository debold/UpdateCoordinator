using Microsoft.Playwright;

namespace SUSyphos.UiTests;

/// <summary>
/// Checks a rendered page against the layout rules of the dashboard design (docs/06-dashboard.md): nothing makes the page
/// scroll sideways, no text is cut off, nothing sticks out of its box, tile or action band, controls do not overlap and
/// every button has a label. Tables may scroll inside their box only where the window is narrow.
/// </summary>
internal static class LayoutAudit
{
    /// <summary>Windows at least this wide must show every table without horizontal scrolling.</summary>
    public const int WideWidth = 1280;

    private const string Script = """
        (allowTableScroll) => {
            const issues = [];
            const doc = document.documentElement;
            const visible = (el) => {
                const r = el.getBoundingClientRect();
                const s = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
            };
            const describe = (el) => {
                const text = (el.innerText || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim().replace(/\s+/g, ' ').slice(0, 60);
                const cls = typeof el.className === 'string' && el.className.trim() ? '.' + el.className.trim().split(/\s+/).join('.') : '';
                return `${el.tagName.toLowerCase()}${cls} "${text}"`;
            };

            if (doc.scrollWidth > doc.clientWidth + 1) {
                issues.push(`page scrolls sideways: ${doc.scrollWidth}px content in a ${doc.clientWidth}px window`);
            }

            for (const el of document.querySelectorAll('main *')) {
                if (!visible(el) || el.closest('.scroll, details:not([open]) > :not(summary), .week, svg, select, .visually-hidden')) {
                    continue;
                }

                const s = getComputedStyle(el);
                // Text cut with "…" is allowed only where the full text is the tooltip (tile footers, long cells).
                const tooltip = s.textOverflow === 'ellipsis' && (el.getAttribute('title') || '').trim() === el.textContent.trim();
                if ((s.overflowX === 'hidden' || s.overflowX === 'clip' || s.textOverflow === 'ellipsis') && el.scrollWidth > el.clientWidth + 1 && !tooltip) {
                    issues.push(`text cut off: ${describe(el)}`);
                }

                const box = el.parentElement?.closest('.box, .tile, .action-band, .dialog, .restart-banner, .filter-pop');
                if (box) {
                    const r = el.getBoundingClientRect();
                    const b = box.getBoundingClientRect();
                    if (r.right > b.right + 1 || r.left < b.left - 1) {
                        issues.push(`sticks out of ${box.className.split(' ')[0]}: ${describe(el)}`);
                    }
                }
            }

            // Adding to a list: the fields and "+ Add" of a row end on one line, so no hint below a field shifts it (Marc, 2026-10-27).
            for (const row of document.querySelectorAll('main .add-row')) {
                const controls = [...row.querySelectorAll(':scope > .field input, :scope > .field select, :scope > button, :scope > .btn')].filter(visible);
                // Controls side by side (their heights overlap) must end at the same height; a wrapped narrow row is fine.
                const rects = controls.map((c) => c.getBoundingClientRect());
                const off = controls.filter((c, i) => rects.some((r, j) => j !== i && r.top < rects[i].bottom && rects[i].top < r.bottom && Math.abs(r.bottom - rects[i].bottom) > 2));
                if (off.length > 0) {
                    issues.push(`fields of an add row do not line up: ${off.map(describe).join(', ')}`);
                }
            }

            if (!allowTableScroll) {
                for (const sc of document.querySelectorAll('main .scroll')) {
                    if (visible(sc) && sc.scrollWidth > sc.clientWidth + 1) {
                        const title = sc.closest('.box')?.querySelector('h2')?.innerText ?? 'table';
                        issues.push(`table needs sideways scrolling: "${title}" (${sc.scrollWidth}px in ${sc.clientWidth}px)`);
                    }
                }
            }

            for (const el of document.querySelectorAll('button, a.btn, summary')) {
                if (el.closest('details:not([open])') && !el.closest('summary')) {
                    continue;
                }

                if (visible(el) && !((el.innerText || '').trim() || el.getAttribute('aria-label') || el.getAttribute('title'))) {
                    issues.push(`control without label: ${el.outerHTML.slice(0, 100)}`);
                }
            }

            for (const group of document.querySelectorAll('.action-band, .figures, .live-line, .window-row')) {
                const kids = [...group.children].filter(visible);
                for (let i = 0; i < kids.length; i++) {
                    for (let j = i + 1; j < kids.length; j++) {
                        const a = kids[i].getBoundingClientRect();
                        const b = kids[j].getBoundingClientRect();
                        if (a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1) {
                            issues.push(`overlap: ${describe(kids[i])} and ${describe(kids[j])}`);
                        }
                    }
                }
            }

            return [...new Set(issues)].slice(0, 40);
        }
        """;

    public static async Task<IReadOnlyList<string>> RunAsync(IPage page, int width) =>
        await page.EvaluateAsync<string[]>(Script, width < WideWidth);
}
