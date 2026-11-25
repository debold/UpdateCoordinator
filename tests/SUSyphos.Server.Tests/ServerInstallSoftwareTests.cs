using System.Net;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The dialog "Install software…" of the server page: pages of ten, packages the server cannot install grey.</summary>
public sealed class ServerInstallSoftwareTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Dialog_ShowsTenPackagesPerPage_AndUnsupportedOnesGreyWithTheReason()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(db =>
        {
            for (var i = 1; i <= 12; i++)
            {
                db.Packages.Add(new PackageEntity { PackageId = $"Vendor.Tool{i:00}", Name = $"Tool {i:00}", AddedBy = "admin" });
                db.PackageVersions.Add(new PackageVersionEntity { PackageId = $"Vendor.Tool{i:00}", Version = "1.0", UpdateId = Guid.NewGuid(), Manifest = "{\"Installers\":[{\"InstallerType\":\"msi\"}]}" });
            }

            db.Packages.Add(new PackageEntity { PackageId = "Vendor.Future", Name = "A future tool", AddedBy = "admin" });
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = "Vendor.Future", Version = "2.0", UpdateId = Guid.NewGuid(), Manifest = "{\"Installers\":[{\"InstallerType\":\"msi\",\"MinimumOSVersion\":\"10.0.99999.0\"}]}" });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var html = WebUtility.HtmlDecode(await (await admin.GetAsync($"/servers/{server.ClientId}?tab=software&dialog=install-software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(10, html.Split("name=\"InstallPackage\"").Length - 1);
        Assert.Contains("<tr class=\"unavailable\">", html, StringComparison.Ordinal);
        Assert.Contains("Not supported here: Needs Windows 10.0.99999.0 or later", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Pages\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool 12", html, StringComparison.Ordinal);

        var second = WebUtility.HtmlDecode(await (await admin.GetAsync($"/servers/{server.ClientId}?tab=software&dialog=install-software&sw.page=2")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Tool 12", second, StringComparison.Ordinal);
        Assert.Equal(3, second.Split("name=\"InstallPackage\"").Length - 1);

        // The search narrows the catalog before it is paged.
        var search = WebUtility.HtmlDecode(await (await admin.GetAsync($"/servers/{server.ClientId}?tab=software&dialog=install-software&swq=tool%2003")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, search.Split("name=\"InstallPackage\"").Length - 1);
    }
}
