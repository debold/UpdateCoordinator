using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Administration;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>
/// Development aid, not a check: writes the rendered dashboard pages with example data as standalone HTML files for a
/// visual review. Runs only when SUSYPHOS_SNAPSHOTS names an output folder.
/// </summary>
public sealed partial class DashboardSnapshots
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task WritePages()
    {
        var output = Environment.GetEnvironmentVariable("SUSYPHOS_SNAPSHOTS");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Set SUSYPHOS_SNAPSHOTS to an output folder to write dashboard snapshots.");
        Directory.CreateDirectory(output!);

        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("marc", Password, AdminRoles.Admin);
        await DemoData.SeedAsync(factory);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("marc", Password))
        {
        }

        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SUSyphos.Server", "wwwroot", "app.css"), TestContext.Current.CancellationToken);
        var pages = (Environment.GetEnvironmentVariable("SUSYPHOS_SNAPSHOT_PAGES") ?? "/;/servers;/servers?group=os;/servers/{sql01};/servers/{sql01}?dialog=remove")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var sql01 = await factory.WithDbAsync(db => db.Clients.Where(c => c.HostName == "SQL01").Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));
        var index = 0;
        foreach (var page in pages)
        {
            using var response = await browser.GetAsync(page.Replace("{sql01}", sql01.ToString(), StringComparison.Ordinal));
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            html = StylesheetRegex().Replace(html, $"<style>{css}</style>");
            await File.WriteAllTextAsync(Path.Combine(output!, $"page-{index++:00}.html"), $"<!-- {page} -->\n{html}", TestContext.Current.CancellationToken);
        }
    }

    [GeneratedRegex("<link rel=\"stylesheet\"[^>]*>")]
    private static partial Regex StylesheetRegex();
}
