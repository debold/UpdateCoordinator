using System.Text.Json;

namespace SUSyphos.UiTests;

/// <summary>
/// The text of pills, category tags and tab counts sits in the optical middle of its box with every font, not only with
/// the one on the developer's machine: Windows Server 2016 to 2022 have Segoe UI, Windows 11 and Server 2025 Segoe UI
/// Variable, and they sit differently in their line boxes (Marc, 2026-11-08). The middle of the capital letters must lie
/// within a pixel of the middle of the box.
/// </summary>
public sealed class PillAlignmentTests(UiFixture ui)
{
    private const double Tolerance = 1.25;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private const string Measure =
        """
        (sel) => {
          const ctx = document.createElement('canvas').getContext('2d');
          const out = [];
          for (const el of document.querySelectorAll(sel)) {
            if (!el.getClientRects().length) continue;
            const target = el.querySelector('.pill-text') || el;
            const node = [...target.childNodes].find(n => n.nodeType === 3 && n.textContent.trim());
            if (!node) continue;
            const cs = getComputedStyle(target);
            ctx.font = `${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
            const m = ctx.measureText('H');
            const range = document.createRange();
            range.selectNodeContents(node);
            const line = range.getClientRects()[0];
            if (!line) continue;
            const capMiddle = line.top + m.fontBoundingBoxAscent - m.actualBoundingBoxAscent / 2;
            const box = el.getBoundingClientRect();
            out.push({ text: node.textContent.trim(), offset: capMiddle - (box.top + box.height / 2) });
          }
          return JSON.stringify(out);
        }
        """;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var font in new[] { "default", "Segoe UI", "Arial" })
        {
            foreach (var path in new[] { "/updates", "/servers/{main}?tab=history&wu.view=all&hist.view=all", "/servers/{main}?tab=system", "/updates/{update}" })
            {
                data.Add(path, font);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Text_SitsInTheMiddleOfItsPill(string path, string font)
    {
        await using var context = await ui.NewContextAsync(1440, 900);
        var page = await context.NewPageAsync();
        await page.GotoAsync(await new PageLayoutTests(ui).ResolveAsync(path));
        if (font != "default")
        {
            // The content security policy refuses a style element; the CSSOM is allowed and so is a font for every element.
            await page.EvaluateAsync("f => { for (const e of [document.documentElement, ...document.querySelectorAll('*')]) e.style.setProperty('font-family', f, 'important'); }", $"\"{font}\", sans-serif");
        }

        var json = await page.EvaluateAsync<string>(Measure, "main .pill, main .cat-tag, main .tab-badge");
        var items = JsonSerializer.Deserialize<List<Measured>>(json, Options)!;

        Assert.NotEmpty(items);
        var off = items.Where(item => Math.Abs(item.Offset) > Tolerance).Select(item => $"'{item.Text}' {item.Offset:+0.0;-0.0} px").ToList();
        Assert.True(off.Count == 0, $"Text off the middle with font {font} on {path}: {string.Join(", ", off)}");
    }

    private sealed record Measured(string Text, double Offset);
}
