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
        // The installed version tells the agent (and its scripts) that this is an upgrade.
        Assert.Equal(new SoftwareRelease("7zip.7zip", "26.06", false, InstalledVersion: "26.05"), release.Software);
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
    public async Task VersionBeingCached_ShowsTheStep_AndAdministratorsCanStartTheCache()
    {
        // Marc, 2026-11-23: the banner said nothing about what the cache was doing.
        await using var scenario = await RolloutScenario.CreateAsync();
        await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);
        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).ExecuteUpdateAsync(s => s.SetProperty(v => v.CachedAtUtc, (DateTime?)null).SetProperty(v => v.ScanState, InstallerScanState.NotScanned), TestContext.Current.CancellationToken));
        var progress = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SUSyphos.Server.Software.SoftwareCacheProgress>(scenario.Factory.Services);
        var version = await scenario.Factory.WithDbAsync(db => db.PackageVersions.SingleAsync(v => v.UpdateId == NewZip, TestContext.Current.CancellationToken));
        await scenario.Factory.CreateUserAsync("admin", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Admin);
        await scenario.Factory.CreateUserAsync("reader", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Reader);
        using var admin = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", "correct horse battery"))
        {
        }

        using var reader = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await reader.LoginAsync("reader", "correct horse battery"))
        {
        }

        var waiting = System.Net.WebUtility.HtmlDecode(await (await admin.GetAsync($"/updates/{NewZip:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("waits for the next run of the installer cache", waiting, StringComparison.Ordinal);
        Assert.Contains("value=\"check-now\"", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("Open package", waiting, StringComparison.Ordinal);
        Assert.Contains("susyphos-refresh", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"check-now\"", await (await reader.GetAsync($"/updates/{NewZip:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        progress.Downloading(version.PackageId, version.Version);
        progress.Received(version.PackageId, version.Version, 1_048_576, 2_097_152);
        var downloading = System.Net.WebUtility.HtmlDecode(await (await admin.GetAsync($"/updates/{NewZip:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(": 1.0 of 2.0 MB", downloading, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"check-now\"", downloading, StringComparison.Ordinal);

        progress.Scanning(version.PackageId, version.Version);
        Assert.Contains("is being scanned for viruses since", System.Net.WebUtility.HtmlDecode(await (await admin.GetAsync($"/updates/{NewZip:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken)), StringComparison.Ordinal);
        progress.Done(version.PackageId, version.Version);
    }

    [Fact]
    public void FailedDownload_IsExplainedWithItsReason()
    {
        var version = new PackageVersionEntity { PackageId = "7zip.7zip", Version = "26.06", ScanState = InstallerScanState.NotScanned, ScanDetail = "Download failed: example.com answered 404." };

        Assert.Contains("The last attempt to download the installer failed (Download failed: example.com answered 404.)", SUSyphos.Server.Software.SoftwareSource.Explain(version), StringComparison.Ordinal);
        Assert.Equal("the download of the installer failed, it is tried again", SUSyphos.Server.Software.SoftwareSource.NotOfferedReason(version));
    }

    [Fact]
    public async Task CheckNow_EndsTheWaitOfTheCache()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        using var progress = new SUSyphos.Server.Software.SoftwareCacheProgress(time);
        var wait = progress.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddMinutes(5), progress.NextRunUtc);

        progress.RequestRun();
        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Without a new request the next wait lasts its interval.
        var next = progress.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(5));
        await next.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VersionWithAFailedScan_SaysWhyTheServersWait()
    {
        // Lab, Marc 2026-11-20: released to every ring, the servers stayed "queued" without a word while the scan failed.
        await using var scenario = await RolloutScenario.CreateAsync();
        await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);
        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).ExecuteUpdateAsync(s => s
            .SetProperty(v => v.CachedAtUtc, (DateTime?)null)
            .SetProperty(v => v.ScanState, InstallerScanState.Failed)
            .SetProperty(v => v.ScanDetail, "setup.exe: Windows could not scan the file"), TestContext.Current.CancellationToken));
        await scenario.WithServiceAsync<ReleasePlanner, bool>(async p => { await p.SyncAsync(null, CancellationToken.None); return true; });
        await scenario.Factory.CreateUserAsync("reader", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", "correct horse battery"))
        {
        }

        var page = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync($"/updates/{NewZip:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var list = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync("/updates")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Not in the WinGet source", page, StringComparison.Ordinal);
        Assert.Contains("setup.exe: Windows could not scan the file", page, StringComparison.Ordinal);
        Assert.Contains("not in the WinGet source yet", page, StringComparison.Ordinal);
        Assert.DoesNotContain("no server needs it", page, StringComparison.Ordinal);
        Assert.Contains("not in the WinGet source: the virus scan of the installer failed", list, StringComparison.Ordinal);

        // Once cached, nothing of it is left.
        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).ExecuteUpdateAsync(s => s.SetProperty(v => v.CachedAtUtc, DateTime.UtcNow).SetProperty(v => v.ScanState, InstallerScanState.Clean), TestContext.Current.CancellationToken));
        var cached = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync($"/updates/{NewZip:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Not in the WinGet source", cached, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ScanAgain_DiscardsAWrongFinding_ForAdministratorsOnly()
    {
        // rc.7 lab, Marc 2026-11-22: WebView2 158 stood on "Threat found" without any finding of Defender.
        await using var scenario = await RolloutScenario.CreateAsync();
        await AddServerWithZipAsync(scenario, "OLD01", "26.05");
        await StartRolloutAsync(scenario);
        await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).ExecuteUpdateAsync(s => s
            .SetProperty(v => v.CachedAtUtc, (DateTime?)null)
            .SetProperty(v => v.ScanState, InstallerScanState.Detected)
            .SetProperty(v => v.ScanDetail, "setup.exe: The antivirus product removed the file while scanning it"), TestContext.Current.CancellationToken));
        await scenario.Factory.CreateUserAsync("operator", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Operator);
        await scenario.Factory.CreateUserAsync("admin", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Admin);
        var path = $"/updates/{NewZip:D}/1";
        using var operatorBrowser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await operatorBrowser.LoginAsync("operator", "correct horse battery"))
        {
        }

        Assert.DoesNotContain("value=\"scan-again\"", await (await operatorBrowser.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using (await operatorBrowser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "update", ["Action"] = "scan-again" }))
        {
        }

        Assert.Equal(InstallerScanState.Detected, await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).Select(v => v.ScanState).SingleAsync(TestContext.Current.CancellationToken)));

        using var admin = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await admin.LoginAsync("admin", "correct horse battery"))
        {
        }

        Assert.Contains("value=\"scan-again\"", await (await admin.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using (await admin.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "update", ["Action"] = "scan-again" }))
        {
        }

        Assert.Equal(InstallerScanState.NotScanned, await scenario.Factory.WithDbAsync(db => db.PackageVersions.Where(v => v.UpdateId == NewZip).Select(v => v.ScanState).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Package.ScanAgain", TestContext.Current.CancellationToken)));
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
        Assert.Contains("class=\"cat-tag c9\" title=\"Software\">Software</span>", history, StringComparison.Ordinal);
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

    [Fact]
    public async Task OlderOfTwoPlatforms_IsUpdated_InItsOwnArchitecture()
    {
        // Lab, 2026-11-16: x64 10.0.12 and x86 10.0.15 of the .NET runtime; the release must update the x64 one.
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("APP01", RingEntity.PilotId);
        const string Runtime = "Microsoft.DotNet.DesktopRuntime.10";
        var updateId = SoftwarePackages.SoftwareUpdateId(Runtime, "10.0.15");
        await scenario.Factory.WithDbAsync(async db =>
        {
            db.Packages.Add(new PackageEntity { PackageId = Runtime, Name = ".NET Desktop Runtime 10", AddedBy = "inventory" });
            db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = SoftwarePackages.SoftwareRevision, Title = ".NET Desktop Runtime 10 10.0.15", Kind = UpdateKind.Software, PackageId = Runtime, PackageVersion = "10.0.15" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client.ClientId, View = SoftwareView.Machine32, KeyName = "{x64}", DisplayName = "Microsoft Windows Desktop Runtime 10.0.12 (x64)", PackageId = Runtime, InstalledVersion = "10.0.12.50000", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client.ClientId, View = SoftwareView.Machine32, KeyName = "{x86}", DisplayName = "Microsoft Windows Desktop Runtime 10.0.15 (x86)", PackageId = Runtime, InstalledVersion = "10.0.15", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            db.Releases.Add(new ReleaseEntity { ClientId = client.ClientId, UpdateId = updateId, Revision = SoftwarePackages.SoftwareRevision, EarliestInstallAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var software = Assert.Single((await scenario.ReleasesAsync(client)).Releases).Software!;

        Assert.Equal(("10.0.12.50000", "x64"), (software.InstalledVersion, software.Architecture));
    }

    [Fact]
    public async Task EntryLeftBehindByAnUpgrade_DoesNotMakeTheVersionMissing()
    {
        // Lab, 2026-11-21: Visual C++ 2015+ x64 14.51 stayed registered next to 14.52 after the upgrade had to wait for a
        // restart; counted on its own it asked for 14.52 forever.
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("DC01", RingEntity.PilotId);
        const string Runtime = "Microsoft.VCRedist.2015+.x64";
        var updateId = SoftwarePackages.SoftwareUpdateId(Runtime, "14.52.37029.0");
        await scenario.Factory.WithDbAsync(async db =>
        {
            db.Packages.Add(new PackageEntity { PackageId = Runtime, Name = "Visual C++ v14 Redistributable (x64)", AddedBy = "inventory" });
            db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = SoftwarePackages.SoftwareRevision, Title = "Visual C++ 14.52", Kind = UpdateKind.Software, PackageId = Runtime, PackageVersion = "14.52.37029.0" });
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = Runtime, Version = "14.52.37029.0", UpdateId = updateId });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client.ClientId, View = SoftwareView.Machine32, KeyName = "{old}", DisplayName = "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", PackageId = Runtime, InstalledVersion = "14.51.36247.0", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client.ClientId, View = SoftwareView.Machine32, KeyName = "{new}", DisplayName = "Microsoft Visual C++ v14 Redistributable (x64) - 14.52.37029", PackageId = Runtime, InstalledVersion = "14.52.37029.0", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var (needed, installed) = await scenario.WithServiceAsync<ServerDbContext, (IReadOnlyList<SoftwareNeed>, IReadOnlyList<SoftwareNeed>)>(db => SoftwareNeeds.LoadAsync(db, [client.ClientId], CancellationToken.None));

        Assert.Empty(needed);
        Assert.Equal([updateId], installed.Select(i => i.UpdateId));
    }

    [Fact]
    public async Task OlderVersionOfTheOtherArchitecture_IsStillNeeded()
    {
        // Negative: x86 14.51 next to x64 14.52 is a second installation, not an entry left behind.
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("APP02", RingEntity.PilotId);
        const string Runtime = "Microsoft.VCRedist.2015+.x64";
        var updateId = SoftwarePackages.SoftwareUpdateId(Runtime, "14.52.37029.0");
        await scenario.Factory.WithDbAsync(async db =>
        {
            db.Packages.Add(new PackageEntity { PackageId = Runtime, Name = "Visual C++ v14 Redistributable", AddedBy = "inventory" });
            db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = SoftwarePackages.SoftwareRevision, Title = "Visual C++ 14.52", Kind = UpdateKind.Software, PackageId = Runtime, PackageVersion = "14.52.37029.0" });
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = Runtime, Version = "14.52.37029.0", UpdateId = updateId });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client.ClientId, View = SoftwareView.Machine32, KeyName = "{x86}", DisplayName = "Microsoft Visual C++ v14 Redistributable (x86) - 14.51.36247", PackageId = Runtime, InstalledVersion = "14.51.36247.0", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client.ClientId, View = SoftwareView.Machine32, KeyName = "{x64}", DisplayName = "Microsoft Visual C++ v14 Redistributable (x64) - 14.52.37029", PackageId = Runtime, InstalledVersion = "14.52.37029.0", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var (needed, _) = await scenario.WithServiceAsync<ServerDbContext, (IReadOnlyList<SoftwareNeed>, IReadOnlyList<SoftwareNeed>)>(db => SoftwareNeeds.LoadAsync(db, [client.ClientId], CancellationToken.None));

        Assert.Equal([updateId], needed.Select(n => n.UpdateId));
    }

    [Fact]
    public async Task VersionTheServerCannotInstall_IsNotNeeded_AndSaysWhy()
    {
        // Lab, 2026-11-20: BGInfo (portable) on Windows Server 2019 failed with 0x8A150010 again and again.
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("DC01", RingEntity.PilotId);
        const string Tool = "Microsoft.Sysinternals.BGInfo";
        var updateId = SoftwarePackages.SoftwareUpdateId(Tool, "4.34");
        await scenario.Factory.WithDbAsync(async db =>
        {
            await db.Clients.Where(c => c.Id == client.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.OsVersion, "10.0.17763.9487"), TestContext.Current.CancellationToken);
            db.Packages.Add(new PackageEntity { PackageId = Tool, Name = "BGInfo", AddedBy = "admin" });
            db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = SoftwarePackages.SoftwareRevision, Title = "BGInfo 4.34", Kind = UpdateKind.Software, PackageId = Tool, PackageVersion = "4.34" });
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = Tool, Version = "4.34", UpdateId = updateId, Manifest = "{\"Installers\":[{\"Architecture\":\"x64\",\"InstallerType\":\"zip\",\"NestedInstallerType\":\"portable\"}]}" });
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client.ClientId, View = SoftwareView.Machine64, KeyName = "BGInfo", DisplayName = "BGInfo", PackageId = Tool, InstalledVersion = "4.33", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var (needed, _) = await scenario.WithServiceAsync<ServerDbContext, (IReadOnlyList<SoftwareNeed>, IReadOnlyList<SoftwareNeed>)>(db => SoftwareNeeds.LoadAsync(db, [client.ClientId], CancellationToken.None));
        var unsupported = await scenario.WithServiceAsync<ServerDbContext, IReadOnlyList<(SoftwareNeed Need, string Reason)>>(db => SoftwareNeeds.UnsupportedAsync(db, [client.ClientId], CancellationToken.None));

        Assert.Empty(needed);
        Assert.Contains("portable", Assert.Single(unsupported).Reason, StringComparison.Ordinal);

        // The version's page counts them in an amber banner with a link, and the Servers tab names the state and the reason.
        await scenario.Factory.CreateUserAsync("reader", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", "correct horse battery"))
        {
        }

        var page = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync($"/updates/{updateId:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("<div class=\"banner queue help-scope\" role=\"status\"><span class=\"pill queue\">Not supported on some servers</span>", page, StringComparison.Ordinal);
        Assert.Contains("1 server cannot install this version", page, StringComparison.Ordinal);
        Assert.DoesNotContain("DC01 (", page, StringComparison.Ordinal);
        Assert.Contains("f.status=Not%20supported#servers", page, StringComparison.Ordinal);
        var servers = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync($"/updates/{updateId:D}/1?tab=servers&f.status=Not%20supported")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Matches("<span class=\"pill queue\" title=\"[^\"]*portable[^\"]*\">Not supported</span>", servers);
        Assert.Contains("DC01", servers, StringComparison.Ordinal);
        Assert.DoesNotContain(">Not needed<", servers, StringComparison.Ordinal);

        // The same server on Windows Server 2022 needs it.
        await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == client.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.OsVersion, "10.0.20348.5870"), TestContext.Current.CancellationToken));
        (needed, _) = await scenario.WithServiceAsync<ServerDbContext, (IReadOnlyList<SoftwareNeed>, IReadOnlyList<SoftwareNeed>)>(db => SoftwareNeeds.LoadAsync(db, [client.ClientId], CancellationToken.None));
        Assert.Equal([updateId], needed.Select(n => n.UpdateId));
    }

    [Fact]
    public async Task ServerWithoutWinGet_IsNotSupported_OnTheVersionPage_WithoutTheBanner()
    {
        // Lab, 2026-11-25: Windows Server 2016 showed "Not needed" for PowerShell 7, although SUSyphos cannot install
        // software there at all.
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await scenario.AddClientAsync("FS01", RingEntity.PilotId);
        await scenario.AddClientAsync("APP01", RingEntity.PilotId);
        const string Tool = "Microsoft.PowerShell";
        var updateId = SoftwarePackages.SoftwareUpdateId(Tool, "7.7.0.0");
        await scenario.Factory.WithDbAsync(async db =>
        {
            await db.Clients.Where(c => c.Id == old.ClientId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.SoftwareState, SoftwareSupportState.NotSupported)
                .SetProperty(c => c.SoftwareDetail, "WinGet needs Windows Server 2019 or later."), TestContext.Current.CancellationToken);
            db.Packages.Add(new PackageEntity { PackageId = Tool, Name = "PowerShell", AddedBy = "admin" });
            db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = SoftwarePackages.SoftwareRevision, Title = "PowerShell 7.7.0.0", Kind = UpdateKind.Software, PackageId = Tool, PackageVersion = "7.7.0.0" });
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = Tool, Version = "7.7.0.0", UpdateId = updateId, Manifest = "{\"Installers\":[{\"Architecture\":\"x64\",\"InstallerType\":\"msix\"}]}" });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await scenario.Factory.CreateUserAsync("reader", "correct horse battery", SUSyphos.Server.Administration.AdminRoles.Reader);
        using var browser = new SUSyphos.TestSupport.DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", "correct horse battery"))
        {
        }

        var page = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync($"/updates/{updateId:D}/1")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Not supported on some servers", page, StringComparison.Ordinal);
        var servers = System.Net.WebUtility.HtmlDecode(await (await browser.GetAsync($"/updates/{updateId:D}/1?tab=servers&view=all")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("<span class=\"pill queue\" title=\"WinGet is not supported on this server: WinGet needs Windows Server 2019 or later.\">Not supported</span>", servers, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(servers, ">Not supported</span>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(servers, ">Not needed</span>"));
    }
}
