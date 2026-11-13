using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The Software page, the package page and the Software tab of a server.</summary>
public sealed class SoftwarePageTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly ServerFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<T> ServiceAsync<TService, T>(Func<TService, Task<T>> action)
        where TService : notnull
    {
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<(DashboardClient Browser, Guid Server)> SeedAsync(string role)
    {
        using var http = _factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);
        await _factory.WithDbAsync(async db =>
        {
            var client = await db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken);
            client.State = SUSyphos.Contracts.Agents.RegistrationState.Approved;
            client.HostName = "SQL01";
            client.SoftwareState = SUSyphos.Contracts.Agents.SoftwareSupportState.Supported;
            DemoSoftware.Add(db, [client.Id], [client.Id], DateTime.UtcNow);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        await _factory.CreateUserAsync("user", Password, role);
        var browser = new DashboardClient(_factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        return (browser, credentials.ClientId);
    }

    [Fact]
    public async Task SoftwarePage_ListsThePackages_WithTheirState()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Reader);
        using (browser)
        {
            var html = await (await browser.GetAsync("/software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains("href=\"/software/7zip.7zip\"", html, StringComparison.Ordinal);
            Assert.Contains("Agreements not accepted", html, StringComparison.Ordinal);
            Assert.Contains("1 server runs an older one", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Add package", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PackagePage_ShowsVersionsAndServers_AndOnlyHttpsLinks()
    {
        var (browser, server) = await SeedAsync(AdminRoles.Reader);
        using (browser)
        {
            var html = await (await browser.GetAsync("/software/7zip.7zip")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains("href=\"https://www.7-zip.org/\" target=\"_blank\" rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
            Assert.Contains($"/updates/{SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06")}/1", html, StringComparison.Ordinal);
            Assert.Contains($"/servers/{server}?tab=software#software", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Accept agreements", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnknownPackage_SaysSo()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Reader);
        using (browser)
        {
            using var response = await browser.GetAsync("/software/Unknown.Package");

            Assert.Contains("Package not found", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Admin_AcceptsTheAgreements_Audited()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Admin);
        using (browser)
        {
            const string Path = "/software/Microsoft.SQLServerManagementStudio";
            Assert.Contains("Accept agreements", await (await browser.GetAsync(Path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

            using var response = await browser.PostFormAsync(Path, new Dictionary<string, string> { ["_handler"] = "package-accept" });

            Assert.Contains("The agreements were accepted", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            var package = await _factory.WithDbAsync(db => db.Packages.SingleAsync(p => p.PackageId == "Microsoft.SQLServerManagementStudio", TestContext.Current.CancellationToken));
            Assert.Equal("user", package.AgreementsAcceptedBy);
            Assert.True(await _factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Package.AgreementsAccepted", TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task Operator_CannotAcceptAgreementsOrAddPackages()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Operator);
        using (browser)
        {
            // Operators never get the forms; a forged post is refused (form not rendered, or "Only administrators").
            using var accept = await browser.PostFormAsync("/software/Microsoft.SQLServerManagementStudio", new Dictionary<string, string> { ["_handler"] = "package-accept" });
            using var add = await browser.PostFormAsync("/software?dialog=add", new Dictionary<string, string> { ["_handler"] = "software-add", ["PackageId"] = "Notepad++.Notepad++" });

            Assert.DoesNotContain("The agreements were accepted", await accept.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Contains("Only administrators", await add.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Null(await _factory.WithDbAsync(db => db.Packages.Where(p => p.PackageId == "Microsoft.SQLServerManagementStudio").Select(p => p.AgreementsAcceptedAtUtc).SingleAsync(TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task Admin_AddingWithoutIndex_GetsTheReason()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Admin);
        using (browser)
        {
            using var add = await browser.PostFormAsync("/software?dialog=add", new Dictionary<string, string> { ["_handler"] = "software-add", ["PackageId"] = "Contoso.Tool" });

            // The reason shows inside the dialog, not hidden behind it.
            var html = await add.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var dialog = html.IndexOf("role=\"dialog\"", StringComparison.Ordinal);
            Assert.True(dialog >= 0);
            Assert.Contains("<p class=\"error\" role=\"alert\">The package index of the WinGet community source has not been loaded yet.", html[dialog..], StringComparison.Ordinal);
        }
    }

    /// <summary>A server whose community source has an index, so a package can be added.</summary>
    private sealed class IndexedFactory(FakeSoftwareSource source) : ServerFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services => services.AddSingleton<ISoftwareSource>(source));
        }
    }

    [Fact]
    public async Task Admin_AddingAPackage_OpensItsPage()
    {
        using var source = new FakeSoftwareSource(Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", "add-" + Guid.NewGuid().ToString("N")));
        source.SetIndex(new TestIndexPackage("Vendor.Tool", "Vendor Tool", "2.0"));
        await using var factory = new IndexedFactory(source);
        factory.StartServer();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        using var add = await browser.PostFormAsync("/software?dialog=add", new Dictionary<string, string> { ["_handler"] = "software-add", ["PackageId"] = "Vendor.Tool" });

        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        var location = add.Headers.Location!.IsAbsoluteUri ? add.Headers.Location.PathAndQuery : add.Headers.Location.ToString();
        Assert.Equal(SUSyphos.Server.Components.PackagePage.Link("Vendor.Tool"), location);
        Assert.Contains("Vendor.Tool", await (await browser.GetAsync(location)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddDialog_PagesTheSearchResults()
    {
        using var source = new FakeSoftwareSource(Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", "add-" + Guid.NewGuid().ToString("N")));
        source.SetIndex([.. Enumerable.Range(1, 25).Select(i => new TestIndexPackage($"Vendor.Tool{i:00}", $"Tool {i:00}", "1.0"))]);
        await using var factory = new IndexedFactory(source);
        factory.StartServer();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var first = await (await browser.GetAsync("/software?dialog=add&q=Vendor")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var third = await (await browser.GetAsync("/software?dialog=add&q=Vendor&add.page=3")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Vendor.Tool01", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Vendor.Tool11", first, StringComparison.Ordinal);
        Assert.Contains("Vendor.Tool21", third, StringComparison.Ordinal);
        Assert.DoesNotContain("Vendor.Tool01", third, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateList_ShowsSoftwareAsSoftware_WithItsPackage()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Reader);
        await _factory.WithDbAsync(async db =>
        {
            var update = await db.Updates.Where(u => u.Kind == UpdateKind.Software).OrderBy(u => u.Title).FirstAsync(TestContext.Current.CancellationToken);
            db.Rollouts.Add(new RolloutEntity { UpdateId = update.UpdateId, Revision = update.Revision, State = RolloutState.Active, StartedAtUtc = DateTime.UtcNow, StartedBy = "admin" });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        using (browser)
        {
            var html = await (await browser.GetAsync("/updates?f.classification=Software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains("class=\"tag\" href=\"/software/", html, StringComparison.Ordinal);
            Assert.Contains("<span class=\"pill neutral\" title=\"Usually none\">usually none</span>", html, StringComparison.Ordinal);
            Assert.DoesNotContain(">Unclassified<", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PackagePage_ShowsHowNewVersionsStart_AndOffersARuleForThePackage()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Admin);
        using (browser)
        {
            var html = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync(SUSyphos.Server.Components.PackagePage.Link("7zip.7zip"))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            Assert.Contains("release-rules?dialog=rule-new&kind=software&package=7zip.7zip", html, StringComparison.Ordinal);
            Assert.Contains("Start rule", html, StringComparison.Ordinal);
            Assert.Contains("Assignments", html, StringComparison.Ordinal);
            Assert.Contains("new versions wait for a manual start", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerSoftwareTab_ShowsPrograms_StateAndSupport()
    {
        var (browser, server) = await SeedAsync(AdminRoles.Reader);
        using (browser)
        {
            using var response = await browser.GetAsync($"/servers/{server}?tab=software");
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Contoso Backup Agent", html, StringComparison.Ordinal);
            Assert.Contains("not known to WinGet", html, StringComparison.Ordinal);
            Assert.Contains("Newer version available", html, StringComparison.Ordinal);
            Assert.Contains("WinGet works on this server", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Admin_SwitchesSoftwareOff_EverythingStops_AndOnAgain()
    {
        var (browser, server) = await SeedAsync(AdminRoles.Admin);
        using (browser)
        {
            var before = await _factory.WithDbAsync(db => db.Clients.Where(c => c.Id == server).Select(c => c.ReleasesVersion).SingleAsync(TestContext.Current.CancellationToken));

            using var off = await browser.PostFormAsync("/software?dialog=off", new Dictionary<string, string> { ["_handler"] = "software-switch", ["Enabled"] = "false" });

            Assert.False(await ServiceAsync<SoftwareFeature, bool>(f => f.IsEnabledAsync(CancellationToken.None)));
            Assert.True(await _factory.WithDbAsync(db => db.Clients.Where(c => c.Id == server).Select(c => c.ReleasesVersion).SingleAsync(TestContext.Current.CancellationToken)) > before);
            Assert.True(await _factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Software.Disabled", TestContext.Current.CancellationToken)));

            foreach (var path in new[] { "/software", "/software/7zip.7zip", $"/servers/{server}?tab=software" })
            {
                var html = await (await browser.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.Contains("class=\"feature-off\"", html, StringComparison.Ordinal);
                Assert.Contains("Software management is switched off", html, StringComparison.Ordinal);
            }

            using var http = _factory.CreateClient();
            using var source = await http.GetAsync(new Uri("/winget/api/information", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, source.StatusCode);

            using var on = await browser.PostFormAsync("/software", new Dictionary<string, string> { ["_handler"] = "software-switch", ["Enabled"] = "true" });

            Assert.True(await ServiceAsync<SoftwareFeature, bool>(f => f.IsEnabledAsync(CancellationToken.None)));
            Assert.DoesNotContain("class=\"feature-off\"", await (await browser.GetAsync("/software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            using var sourceOn = await http.GetAsync(new Uri("/winget/api/information", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, sourceOn.StatusCode);
        }
    }

    [Fact]
    public async Task Operator_CannotSwitchSoftwareOff()
    {
        var (browser, _) = await SeedAsync(AdminRoles.Operator);
        using (browser)
        {
            using var off = await browser.PostFormAsync("/software?dialog=off", new Dictionary<string, string> { ["_handler"] = "software-switch", ["Enabled"] = "false" });

            Assert.True(await ServiceAsync<SoftwareFeature, bool>(f => f.IsEnabledAsync(CancellationToken.None)));
        }
    }

    [Fact]
    public async Task SwitchedOff_NoSoftwareStarts_AndAgentsAreTold()
    {
        var (browser, server) = await SeedAsync(AdminRoles.Admin);
        browser.Dispose();
        await _factory.WithDbAsync(async db =>
        {
            db.Packages.Single(p => p.PackageId == "7zip.7zip").AgreementsAcceptedAtUtc = null;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        await ServiceAsync<SoftwareFeature, bool>(async f => { await f.SetAsync(false, "admin", CancellationToken.None); return true; });
        Assert.True((await ServiceAsync<SUSyphos.Server.Rollouts.RolloutAdministration, Outcome>(a => a.CreateStartRuleAsync(50, null, null, null, StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SUSyphos.Server.Rollouts.SoftwareConditions(UpdateKind.Software)))).Succeeded);
        var update = SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06");

        await ServiceAsync<SUSyphos.Server.Rollouts.RolloutService, bool>(async s => { await s.AutoStartAsync([(update, 1)], CancellationToken.None); return true; });
        var releases = await ServiceAsync<SUSyphos.Server.Agents.UpdateReportService, SUSyphos.Contracts.Agents.ReleaseList>(r => r.GetReleasesAsync(server, CancellationToken.None));

        Assert.False(await _factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == update, TestContext.Current.CancellationToken)));
        Assert.True(releases.SoftwareDisabled);
    }

    [Fact]
    public async Task SelfSignedCertificate_WithoutTheSetting_IsShownOnEverySoftwarePage_UntilSwitchedOn()
    {
        var (browser, server) = await SeedAsync(AdminRoles.Reader);
        using (browser)
        {
            foreach (var page in new[] { "/", "/software", SUSyphos.Server.Components.PackagePage.Link("7zip.7zip") })
            {
                var html = await (await browser.GetAsync(page)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.Contains("The central server uses a self-signed certificate", html, StringComparison.Ordinal);
                Assert.Contains(SoftwareTrust.SettingLink, html, StringComparison.Ordinal);
            }

            // The server page warns only about this server's own failures.
            Assert.DoesNotContain("self-signed certificate", await (await browser.GetAsync($"/servers/{server}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

            Assert.True((await ServiceAsync<SUSyphos.Server.Assignment.AssignmentAdministration, Outcome>(a => a.SetSystemAgentSettingsAsync(new Dictionary<string, string?> { [SoftwareTrust.SettingKey] = "true" }, "admin", CancellationToken.None))).Succeeded);
            Assert.DoesNotContain("self-signed certificate", await (await browser.GetAsync("/software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerFailingBecauseOfTheCertificate_IsNamed_UntilALaterSuccess()
    {
        var (browser, server) = await SeedAsync(AdminRoles.Reader);
        var update = SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06");
        async Task ReportAsync(int hresult, DateTime at) => await _factory.WithDbAsync(async db =>
        {
            db.Installations.Add(new InstallationEntity { ClientId = server, RunId = Guid.NewGuid(), UpdateId = update, Revision = 1, ResultCode = hresult == 0 ? 2 : 4, HResult = hresult, StartedAtUtc = at, FinishedAtUtc = at, Source = "SUSyphos" });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        await ReportAsync(SUSyphos.Server.Agents.HResultCatalog.CertificateNotTrusted, DateTime.UtcNow.AddHours(-1));
        using (browser)
        {
            var software = await (await browser.GetAsync("/software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var detail = await (await browser.GetAsync($"/servers/{server}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("SQL01 could not install software because it does not trust the certificate of the central server", software, StringComparison.Ordinal);
            Assert.Contains("SQL01 could not install software", detail, StringComparison.Ordinal);
            Assert.Contains("does not trust the certificate of the central server", SUSyphos.Server.Agents.HResultCatalog.Describe(SUSyphos.Server.Agents.HResultCatalog.CertificateNotTrusted), StringComparison.Ordinal);

            await ReportAsync(0, DateTime.UtcNow);
            Assert.DoesNotContain("could not install software", await (await browser.GetAsync("/software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }
}