using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Software versions go through the rings like updates: needed by servers with an older version installed.</summary>
public sealed class SoftwareReleaseTests
{
    private static readonly Guid NewZip = SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06");

    private static async Task<RegisterResponse> AddServerWithZipAsync(RolloutScenario scenario, string host, string version)
    {
        var client = await scenario.AddClientAsync(host, RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(async db =>
        {
            if (!await db.Packages.AnyAsync(TestContext.Current.CancellationToken))
            {
                DemoSoftware.Add(db, [], [], DateTime.UtcNow);
            }

            db.ClientSoftware.Add(new ClientSoftwareEntity
            {
                ClientId = client.ClientId,
                View = SoftwareView.Machine64,
                KeyName = "7-Zip",
                DisplayName = "7-Zip",
                PackageId = "7zip.7zip",
                InstalledVersion = version,
                MatchKind = SoftwareMatch.WinGet,
                ReportedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        return client;
    }

    private static async Task StartRolloutAsync(RolloutScenario scenario)
    {
        await scenario.WithServiceAsync<ServerDbContext, bool>(async db =>
        {
            var now = DateTime.UtcNow;
            db.Rollouts.Add(new RolloutEntity { UpdateId = NewZip, Revision = 1, State = RolloutState.Active, StartedAtUtc = now, StartedBy = "admin" });
            await db.PackageVersions.Where(v => v.UpdateId == NewZip).ExecuteUpdateAsync(s => s.SetProperty(v => v.CachedAtUtc, now), TestContext.Current.CancellationToken);
            db.RolloutRings.Add(new RolloutRingEntity { UpdateId = NewZip, Revision = 1, RingId = RingEntity.PilotId, State = RingRolloutState.Released, ReleasedAtUtc = now, StateChangedAtUtc = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        await scenario.WithServiceAsync<ReleasePlanner, bool>(async p => { await p.SyncAsync(null, CancellationToken.None); return true; });
    }

    [Fact]
    public async Task ServerWithAnOlderVersion_GetsTheNewOneReleased_OthersDoNot()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        var current = await AddServerWithZipAsync(scenario, "NEW01", "26.06");

        await StartRolloutAsync(scenario);

        var release = Assert.Single((await scenario.ReleasesAsync(old)).Releases, r => r.UpdateId == NewZip);
        Assert.Equal(new SoftwareRelease("7zip.7zip", "26.06", false), release.Software);
        Assert.DoesNotContain((await scenario.ReleasesAsync(current)).Releases, r => r.UpdateId == NewZip);
    }

    [Fact]
    public async Task VersionNotYetInTheSource_IsReleasedOnlyOnceCached()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);
        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).ExecuteUpdateAsync(s => s.SetProperty(v => v.CachedAtUtc, (DateTime?)null), TestContext.Current.CancellationToken));
        await scenario.WithServiceAsync<ReleasePlanner, bool>(async p => { await p.SyncAsync(null, CancellationToken.None); return true; });

        Assert.DoesNotContain((await scenario.ReleasesAsync(old)).Releases, r => r.UpdateId == NewZip);

        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).ExecuteUpdateAsync(s => s.SetProperty(v => v.CachedAtUtc, DateTime.UtcNow), TestContext.Current.CancellationToken));
        await scenario.WithServiceAsync<ReleasePlanner, bool>(async p => { await p.SyncAsync(null, CancellationToken.None); return true; });

        Assert.Contains((await scenario.ReleasesAsync(old)).Releases, r => r.UpdateId == NewZip);
    }

    [Fact]
    public async Task ResultOfARunWhoseReleaseEnded_IsStillAccepted()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);

        // The release ends while the agent installs (rollout stopped); the result still counts.
        await scenario.Factory.WithDbAsync(db => db.Releases.Where(r => r.UpdateId == NewZip).ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        await scenario.InstallAsync(server, NewZip, InstallResultCode.Failed);

        Assert.True(await scenario.Factory.WithDbAsync(db => db.Installations.AnyAsync(i => i.UpdateId == NewZip, TestContext.Current.CancellationToken)));

        // A result for an update never released to the ring is still refused.
        var other = SoftwarePackages.SoftwareUpdateId("Notepad++.Notepad++", "8.9.9");
        await scenario.InstallAsync(server, other, InstallResultCode.Succeeded);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Installations.AnyAsync(i => i.UpdateId == other, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task InstallerExitCode_IsStored_AndShownWithTheResult()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);
        var report = new InstallReport
        {
            RunId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow,
            Results = [new InstallResult { UpdateId = NewZip, Revision = 1, ResultCode = InstallResultCode.Failed, HResult = unchecked((int)0x8A150006), InstallerExitCode = 2 }],
        };
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SUSyphos.Contracts.ApiRoutes.AgentInstallReport, UriKind.Relative)) { Content = System.Net.Http.Json.JsonContent.Create(report) })
        {
            request.Headers.Authorization = AgentCredentials.ToHeader(server.ClientId, server.ClientSecret);
            using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal(2, await scenario.Factory.WithDbAsync(db => db.Installations.Select(i => i.InstallerExitCode).SingleAsync(TestContext.Current.CancellationToken)));
        await scenario.Factory.CreateUserAsync("reader", "Reader-Passw0rd!", AdminRoles.Reader);
        var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", "Reader-Passw0rd!"))
        {
        }

        var history = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync($"/servers/{server.ClientId}?tab=history&hist.view=all")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Installer exit code 2.", history, StringComparison.Ordinal);
        Assert.Contains("ended with an error exit code", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchedOff_NoSoftwareIsReleased()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await scenario.WithServiceAsync<SoftwareFeature, bool>(async f => { await f.SetAsync(false, "admin", CancellationToken.None); return true; });

        await StartRolloutAsync(scenario);

        var releases = await scenario.ReleasesAsync(old);
        Assert.Empty(releases.Releases);
        Assert.True(releases.SoftwareDisabled);
    }

    [Fact]
    public async Task NeededVersion_StartsByAnAutomaticSoftwareRule_UnneededDoesNot()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        Assert.True((await scenario.AdministerAsync(a => a.CreateStartRuleAsync(50, null, null, "7-Zip*", StartMode.Automatic, 0, "admin", CancellationToken.None, software: new SoftwareConditions(UpdateKind.Software)))).Succeeded);

        await scenario.WithServiceAsync<RolloutService, int>(s => s.AutoStartWaitingAsync(CancellationToken.None));

        Assert.True(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == NewZip, TestContext.Current.CancellationToken)));
        var notepad = SoftwarePackages.SoftwareUpdateId("Notepad++.Notepad++", "8.9.9");
        Assert.False(await scenario.Factory.WithDbAsync(db => db.Rollouts.AnyAsync(r => r.UpdateId == notepad, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SoftwareInstallation_ShowsInTheHistoryAsSoftware_AndInItsView()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(server, update);
        await scenario.InstallAsync(server, NewZip, InstallResultCode.Succeeded);
        await scenario.Factory.WithDbAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.Installations.Add(new InstallationEntity { ClientId = server.ClientId, RunId = Guid.NewGuid(), UpdateId = update.UpdateId, Revision = 1, ResultCode = (int)InstallResultCode.Succeeded, StartedAtUtc = now, FinishedAtUtc = now, Source = "SUSyphos" });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        await scenario.Factory.CreateUserAsync("reader", "Reader-Passw0rd!", AdminRoles.Reader);
        var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", "Reader-Passw0rd!"))
        {
        }

        var history = await (await browser.GetAsync($"/servers/{server.ClientId}?tab=history")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var softwareOnly = await (await browser.GetAsync($"/servers/{server.ClientId}?tab=history&hist.view=software")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("7-Zip 26.06", history, StringComparison.Ordinal);
        Assert.Contains(update.Title, history, StringComparison.Ordinal);
        Assert.Contains("class=\"cat-tag c4\" title=\"Software\">Software</span>", history, StringComparison.Ordinal);
        Assert.Contains("7-Zip 26.06", softwareOnly, StringComparison.Ordinal);
        Assert.DoesNotContain(update.Title, softwareOnly, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallNow_OffersTheChoice_AndSendsItToTheAgent()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);
        await scenario.ReportAsync(server, RolloutScenario.Update("Security Updates"));
        await scenario.Factory.CreateUserAsync("admin", "Admin-Passw0rd!", AdminRoles.Admin);
        var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("admin", "Admin-Passw0rd!"))
        {
        }

        var path = $"/servers/{server.ClientId}";
        var dialog = await (await browser.GetAsync(path + "?dialog=install")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("What to install", dialog, StringComparison.Ordinal);
        Assert.Contains("value=\"software\"", dialog, StringComparison.Ordinal);

        using (var queued = await browser.PostFormAndFollowAsync(path + "?dialog=install", new Dictionary<string, string> { ["_handler"] = "install", ["InstallRestart"] = "now", ["InstallWhat"] = "software" }))
        {
            var page = await queued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("installs the released software", page, StringComparison.Ordinal);
            Assert.Contains("(software only)", page, StringComparison.Ordinal);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(SUSyphos.Contracts.ApiRoutes.AgentActions, UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(server.ClientId, server.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        var action = Assert.Single((await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<AgentActionList>(response.Content, TestContext.Current.CancellationToken))!.Actions);
        Assert.Equal((AgentActionKind.InstallNow, InstallScope.Software), (action.Kind, action.Scope));
    }

    [Fact]
    public async Task Needs_CompareVersionsLikeWinGet_AndSkipBlockedOnes()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var server = await AddServerWithZipAsync(scenario, "OLD01", "9.20");
        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.Version == "26.05").ExecuteUpdateAsync(s => s.SetProperty(v => v.ScanState, InstallerScanState.Detected), TestContext.Current.CancellationToken));

        var (needed, installed) = await scenario.WithServiceAsync<ServerDbContext, (IReadOnlyList<SoftwareNeed>, IReadOnlyList<SoftwareNeed>)>(db => SoftwareNeeds.LoadAsync(db, [server.ClientId], CancellationToken.None));

        Assert.Equal([NewZip], needed.Select(n => n.UpdateId));
        Assert.Empty(installed);
    }
}
