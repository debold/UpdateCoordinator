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

            Assert.Contains("has not been loaded yet", await add.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
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