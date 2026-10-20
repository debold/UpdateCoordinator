using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class ServerHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ClientEntity Client(Action<ClientEntity>? change = null)
    {
        var client = new ClientEntity
        {
            Id = Guid.NewGuid(),
            SecretHash = new byte[32],
            HostName = "SRV01",
            OsVersion = "10.0.20348.1",
            AgentVersion = "1.0.0",
            State = RegistrationState.Approved,
            LastHeartbeatAtUtc = Now.AddMinutes(-1),
        };
        change?.Invoke(client);
        return client;
    }

    [Fact]
    public void HealthyServer_IsOk() => Assert.Equal(ServerHealthState.Ok, ServerHealth.Evaluate(Client(), false, Now));

    [Fact]
    public void PendingServer_IsNotApproved() =>
        Assert.Equal(ServerHealthState.NotApproved, ServerHealth.Evaluate(Client(c => c.State = RegistrationState.Pending), true, Now));

    [Fact]
    public void ServerWithoutRecentHeartbeat_IsOffline()
    {
        Assert.Equal(ServerHealthState.Offline, ServerHealth.Evaluate(Client(c => c.LastHeartbeatAtUtc = Now.AddMinutes(-61)), false, Now));
        Assert.Equal(ServerHealthState.Offline, ServerHealth.Evaluate(Client(c => c.LastHeartbeatAtUtc = null), false, Now));
    }

    [Fact]
    public void FailedInstallationOrCheck_IsFailed_BeforeRebootAndWarnings()
    {
        Assert.Equal(ServerHealthState.Failed, ServerHealth.Evaluate(Client(c => c.RebootPending = true), true, Now));
        var failedCheck = Client(c => c.HealthChecks = [new HealthCheckEntry { Name = "DiskSpace", Status = (int)HealthStatus.Failed }]);
        Assert.Equal(ServerHealthState.Failed, ServerHealth.Evaluate(failedCheck, false, Now));
    }

    [Fact]
    public void RebootPending_ComesBeforeWarnings() =>
        Assert.Equal(ServerHealthState.RebootPending, ServerHealth.Evaluate(Client(c => { c.RebootPending = true; c.WsusServer = "http://wsus"; }), false, Now));

    [Fact]
    public void WarningCheckOrWsus_IsWarning()
    {
        Assert.Equal(ServerHealthState.Warning, ServerHealth.Evaluate(Client(c => c.WsusServer = "http://wsus:8530"), false, Now));
        var warning = Client(c => c.HealthChecks = [new HealthCheckEntry { Name = "DiskSpace", Status = (int)HealthStatus.Warning }]);
        Assert.Equal(ServerHealthState.Warning, ServerHealth.Evaluate(warning, false, Now));
    }

    [Fact]
    public void WindowsInstallingOnItsOwn_IsWarning()
    {
        Assert.Equal(ServerHealthState.Warning, ServerHealth.Evaluate(Client(c => c.AutomaticUpdatesBypass = true), false, Now));
        Assert.Equal(ServerHealthState.Ok, ServerHealth.Evaluate(Client(c => c.AutoUpdateMode = AutomaticUpdateMode.AutoDownloadNotifyInstall), false, Now));
    }
}

public sealed class ChartTests
{
    [Fact]
    public void Segments_AreProportionalAndSkipEmptyStates()
    {
        var segments = Charts.Segments(new DeploymentCounts(Queued: 1, Pending: 0, Successful: 2, Failed: 1), 100).ToList();

        Assert.Equal(["ok", "queue", "fail"], segments.Select(s => s.Class));
        Assert.Equal([50.0, 25.0, 25.0], segments.Select(s => s.Length));
        Assert.Equal([0.0, 50.0, 75.0], segments.Select(s => s.Offset));
    }

    [Fact]
    public void NoEntries_DrawNoSegments() => Assert.Empty(Charts.Segments(DeploymentCounts.Empty, 100));

    [Fact]
    public void Numbers_UseInvariantCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1234.5", Charts.Number(1234.5));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }
}

public sealed class DeploymentStatusTests
{
    [Fact]
    public async Task States_FollowReleaseAndInstallationResults()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var pilot2 = await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        var pilot3 = await scenario.AddClientAsync("PILOT03", RingEntity.PilotId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        var update = RolloutScenario.Update("Security Updates");
        foreach (var client in new[] { pilot, pilot2, pilot3, production })
        {
            await scenario.ReportAsync(client, update);
        }

        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        await scenario.InstallAsync(pilot2, update.UpdateId, InstallResultCode.Failed);

        var entries = await scenario.WithServiceAsync<DeploymentStatus, IReadOnlyList<DeploymentEntry>>(s => s.GetAsync(null, null, null, CancellationToken.None));

        DeploymentState StateOf(RegisterResponse client) => entries.Single(e => e.ClientId == client.ClientId).State;
        Assert.Equal(DeploymentState.Successful, StateOf(pilot));
        Assert.Equal(DeploymentState.Failed, StateOf(pilot2));
        Assert.Equal(DeploymentState.Pending, StateOf(pilot3));
        Assert.Equal(DeploymentState.Queued, StateOf(production));
        Assert.Equal(unchecked((int)0x80070005), entries.Single(e => e.ClientId == pilot2.ClientId).HResult);
        Assert.Equal(new DeploymentCounts(0, 1, 1, 1), DeploymentCounts.Of(entries.Where(e => e.RingId == RingEntity.PilotId)));
    }

    [Fact]
    public async Task UnapprovedServers_AreLeftOut()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.ReportAsync(client, RolloutScenario.Update("Security Updates"));
        await scenario.Factory.WithDbAsync(db => db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Rejected), TestContext.Current.CancellationToken));

        Assert.Empty(await scenario.WithServiceAsync<DeploymentStatus, IReadOnlyList<DeploymentEntry>>(s => s.GetAsync(null, null, null, CancellationToken.None)));
    }
}

public sealed class GlobalPauseTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task OperatorPausesThroughOverview_AndHeartbeatAnnouncesIt()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var paused = await browser.PostFormAsync("/", new Dictionary<string, string> { ["_handler"] = "pause", ["Pause"] = "on" });

        Assert.Contains("Installations are paused.", await paused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True((await HeartbeatAsync(scenario, client)).InstallationsPaused);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Actor == "operator" && e.Action == "Installation.Paused", TestContext.Current.CancellationToken)));

        using (await browser.PostFormAsync("/", new Dictionary<string, string> { ["_handler"] = "pause", ["Pause"] = "off" }))
        {
        }

        Assert.False((await HeartbeatAsync(scenario, client)).InstallationsPaused);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    public async Task Operator_InvalidPauseValue_IsRefused(string value)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("operator", Password))
        {
        }

        using var response = await browser.PostFormAsync("/", new Dictionary<string, string> { ["_handler"] = "pause", ["Pause"] = value });

        Assert.Contains("Invalid request.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(await scenario.WithServiceAsync<GlobalPause, bool>(p => p.IsActiveAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Reader_CannotPause()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.Factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("reader", Password))
        {
        }

        using var page = await browser.GetAsync("/");
        using var response = await browser.PostFormAsync("/", new Dictionary<string, string> { ["_handler"] = "pause", ["Pause"] = "on" });

        Assert.DoesNotContain("Pause all installations", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("Only operators and administrators", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(await scenario.WithServiceAsync<GlobalPause, bool>(p => p.IsActiveAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task PauseForcedByConfiguration_CannotBeLifted()
    {
        await using var factory = new PausedServerFactory();
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        var pause = scope.ServiceProvider.GetRequiredService<GlobalPause>();

        Assert.True(await pause.IsActiveAsync(CancellationToken.None));
        Assert.False((await pause.SetAsync(false, "operator", CancellationToken.None)).Succeeded);
    }

    private static async Task<HeartbeatResponse> HeartbeatAsync(RolloutScenario scenario, RegisterResponse client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0" }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!;
    }

    private sealed class PausedServerFactory : ServerFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Installation:GlobalPause", "true");
        }
    }
}

public sealed class ComplianceHistoryTests
{
    [Fact]
    public async Task Snapshot_CountsServersWithoutOpenReleases_OncePerDay()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        await scenario.ReportAsync(pilot, RolloutScenario.Update("Security Updates"));

        await scenario.WithServiceAsync<ComplianceHistory, int>(async h => { await h.RecordAsync(CancellationToken.None); return 0; });
        await scenario.WithServiceAsync<ComplianceHistory, int>(async h => { await h.RecordAsync(CancellationToken.None); return 0; });

        var snapshot = Assert.Single(await scenario.WithServiceAsync<ComplianceHistory, List<ComplianceSnapshotEntity>>(h => h.GetAsync(90, CancellationToken.None)));
        Assert.Equal(2, snapshot.Servers);
        Assert.Equal(1, snapshot.Compliant);
    }

    [Fact]
    public async Task OldSnapshots_AreRemoved()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-ComplianceHistory.DaysKept - 1);
        await scenario.Factory.WithDbAsync(db =>
        {
            db.ComplianceSnapshots.Add(new ComplianceSnapshotEntity { Day = old, Servers = 1, Compliant = 1 });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await scenario.WithServiceAsync<ComplianceHistory, int>(async h => { await h.RecordAsync(CancellationToken.None); return 0; });

        Assert.False(await scenario.Factory.WithDbAsync(db => db.ComplianceSnapshots.AnyAsync(s => s.Day == old, TestContext.Current.CancellationToken)));
    }
}

public sealed class DashboardViewTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Overview_ShowsAlarmsGatesAndRings()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);
        await scenario.AgeRingAsync(update.UpdateId, RingEntity.PilotId, TimeSpan.FromDays(2));
        await scenario.AdvanceAsync();
        await scenario.Factory.WithDbAsync(db => db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastHeartbeatAtUtc, DateTime.UtcNow), TestContext.Current.CancellationToken));
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        using var response = await browser.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("stopped before ring Test", html, StringComparison.Ordinal);
        Assert.Contains("servers with a failed installation", html, StringComparison.Ordinal);
        Assert.Contains("1 · Pilot", html, StringComparison.Ordinal);
        Assert.Contains("1 failed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RingStatus_CountsServersThatDoNotNeedTheUpdate()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        await scenario.AddClientAsync("PILOT03", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var html = await (await browser.GetAsync($"/rings/{RingEntity.PilotId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("1 successful, 0 pending, 0 queued, 0 failed, 2 not needed", html, StringComparison.Ordinal);
        Assert.Contains("Not needed <b>2</b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_AddUp_AndNeverGoBelowZeroForNotNeeded()
    {
        var sum = new DeploymentCounts(1, 2, 3, 4, 5) + new DeploymentCounts(1, 1, 1, 1);

        Assert.Equal(new DeploymentCounts(2, 3, 4, 5, 5), sum);
        Assert.Equal(19, sum.Total);
        Assert.Equal(0, DeploymentCounts.Of([], 0).NotNeeded);
    }

    [Fact]
    public async Task ServerList_FiltersByShortAgentVersion_AndTheServerPageLinksThere()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var old = await scenario.AddClientAsync("OLD01", RingEntity.PilotId);
        var current = await scenario.AddClientAsync("NEW01", RingEntity.PilotId);
        await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == old.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AgentVersion, "0.5.0-alpha.0.7+abcdef"), TestContext.Current.CancellationToken));
        await scenario.Factory.WithDbAsync(db => db.Clients.Where(c => c.Id == current.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AgentVersion, "0.6.0"), TestContext.Current.CancellationToken));
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var detail = await (await browser.GetAsync($"/servers/{old.ClientId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var link = SUSyphos.Server.Components.Pages.Servers.Link("agent", "0.5.0");
        Assert.Contains($"href=\"{link}\"", detail, StringComparison.Ordinal);
        Assert.Contains(">0.5.0</a>", detail, StringComparison.Ordinal);

        var list = await (await browser.GetAsync(link)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("OLD01", list, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW01", list, StringComparison.Ordinal);
        Assert.Contains("title=\"0.5.0-alpha.0.7", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RingPage_ShowsActiveUpdateAndItsServers()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        using var response = await browser.GetAsync($"/rings/{RingEntity.PilotId}?update={update.UpdateId:D}:1&tab=update");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var members = await (await browser.GetAsync($"/rings/{RingEntity.PilotId}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Pilot</h1>", html, StringComparison.Ordinal);
        Assert.Contains("KB5040000", html, StringComparison.Ordinal);
        Assert.Contains("Status of KB5040000 per server", html, StringComparison.Ordinal);
        Assert.Contains("PILOT01", html, StringComparison.Ordinal);
        Assert.Contains("0x80070005", html, StringComparison.Ordinal);

        // The members of the ring are always the first tab, also before an update is selected.
        Assert.Contains("Servers in this ring (1)", members, StringComparison.Ordinal);
        Assert.Contains("PILOT01", members, StringComparison.Ordinal);
        Assert.DoesNotContain("0x80070005", members, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/rings/99")]
    [InlineData("/rings/1?update=not-an-update")]
    [InlineData("/rings/1?update=%3Cscript%3E")]
    public async Task RingPage_UnknownRingOrUpdate_RendersSafely(string path)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        using var response = await browser.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerList_FiltersByHealthNameAndRing()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var failed = await scenario.AddClientAsync("FAILED01", RingEntity.PilotId);
        await scenario.AddClientAsync("HEALTHY01", RingEntity.TestId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(failed, update);
        await scenario.InstallAsync(failed, update.UpdateId, InstallResultCode.Failed);
        await scenario.Factory.WithDbAsync(db => db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastHeartbeatAtUtc, DateTime.UtcNow), TestContext.Current.CancellationToken));
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var byHealth = await GetHtmlAsync(browser, "/servers?f.status=Failed");
        var byName = await GetHtmlAsync(browser, "/servers?q=healthy");
        var byRing = await GetHtmlAsync(browser, "/servers?f.ring=Pilot");

        Assert.Contains("FAILED01", byHealth, StringComparison.Ordinal);
        Assert.DoesNotContain("HEALTHY01", byHealth, StringComparison.Ordinal);
        Assert.Contains("HEALTHY01", byName, StringComparison.Ordinal);
        Assert.DoesNotContain("FAILED01", byName, StringComparison.Ordinal);
        Assert.Contains("FAILED01", byRing, StringComparison.Ordinal);
        Assert.DoesNotContain("HEALTHY01", byRing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/servers?f.status=Broken&sort=nonsense&dir=sideways&page=abc&group=%3Cx%3E")]
    [InlineData("/servers?page=99999&f.ring=%27%3B--")]
    [InlineData("/servers?q=%25%27%3B--")]
    [InlineData("/servers?q=%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    public async Task ServerList_MalformedFilters_AreHandled(string path)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        using var response = await browser.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("%25")]
    [InlineData("_")]
    [InlineData("SRV%25")]
    public async Task ServerList_SearchWildcards_AreLiteral(string query)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var html = await GetHtmlAsync(browser, $"/servers?q={query}");

        Assert.Contains("No server matches the search and filters.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerList_NonNumericRing_IsIgnored()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        using var response = await browser.GetAsync("/servers?ring=abc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServerDetail_ShowsChecksMissingUpdatesAndHistory()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(client, update);
        await scenario.InstallAsync(client, update.UpdateId, InstallResultCode.Failed);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var html = await GetHtmlAsync(browser, $"/servers/{client.ClientId}");

        Assert.Contains("Disk space", html, StringComparison.Ordinal);
        Assert.Contains("Missing updates", html, StringComparison.Ordinal);
        Assert.Contains("KB5040000", html, StringComparison.Ordinal);
        Assert.Contains("Failed, 0x80070005", html, StringComparison.Ordinal);
    }

    private static async Task<DashboardClient> LoginAsync(RolloutScenario scenario, string role)
    {
        await scenario.Factory.CreateUserAsync("user", Password, role);
        var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        return browser;
    }

    private static async Task<string> GetHtmlAsync(DashboardClient browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class UpdateDetailTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Reader_SeesProgressTimelineAndServers_WithoutActions()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var pilot2 = await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.ReportAsync(pilot2, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var html = await GetHtmlAsync(browser, $"/updates/{update.UpdateId:D}/1");

        Assert.Contains("Progress by ring", html, StringComparison.Ordinal);
        Assert.Contains("(1/2)", html, StringComparison.Ordinal);
        Assert.Contains("Released for ring Pilot", html, StringComparison.Ordinal);
        Assert.Contains("Rollout started", html, StringComparison.Ordinal);
        Assert.Contains("PILOT02", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"stop\"", html, StringComparison.Ordinal);

        using var post = await browser.PostFormAsync($"/updates/{update.UpdateId:D}/1", new Dictionary<string, string> { ["_handler"] = "update", ["Action"] = "stop" }, pageToRead: "/");
        Assert.Equal(RolloutState.Active, await scenario.Factory.WithDbAsync(db => db.Rollouts.Select(r => r.State).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ServerTable_FiltersByState_AndIgnoresUnknownState()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var pilot2 = await scenario.AddClientAsync("PILOT02", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.ReportAsync(pilot2, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Failed);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var failed = await GetHtmlAsync(browser, $"/updates/{update.UpdateId:D}/1?f.status=Failed");
        var unknown = await GetHtmlAsync(browser, $"/updates/{update.UpdateId:D}/1?f.status=%3Cscript%3E&sort=%27");

        Assert.Contains("PILOT01", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("PILOT02", failed, StringComparison.Ordinal);
        // An unknown filter value matches no server instead of being ignored, so a filtered link never shows more than it names.
        Assert.Contains("No server matches.", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerTable_ListsEveryApprovedServerWithItsStatus()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        await scenario.AddClientAsync("OTHER01", RingEntity.TestId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        await scenario.ReportAsync(test, update);
        await scenario.InstallAsync(pilot, update.UpdateId, InstallResultCode.Succeeded);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);
        var path = $"/updates/{update.UpdateId:D}/1";

        var waiting = await GetHtmlAsync(browser, path + "?f.status=Waiting%20for%20release");
        var notNeeded = await GetHtmlAsync(browser, path + "?f.status=Not%20needed");
        var installed = await GetHtmlAsync(browser, path + "?f.status=Installed");

        Assert.Contains("TEST01", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("PILOT01", waiting, StringComparison.Ordinal);
        // A server that never reported the update still appears, so the table covers the whole fleet.
        Assert.Contains("OTHER01", notNeeded, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST01", notNeeded, StringComparison.Ordinal);
        Assert.Contains("PILOT01", installed, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER01", installed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_ApprovesGateAndStopsRolloutOnThePage()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        Assert.True((await scenario.AdministerAsync(admin => admin.SetTransitionAsync(RingEntity.PilotId, 0, 7, 80, 95, true, "admin", CancellationToken.None))).Succeeded);
        var test = await scenario.AddClientAsync("TEST01", RingEntity.TestId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(test, update);
        await scenario.AdvanceAsync();
        using var browser = await LoginAsync(scenario, AdminRoles.Operator);
        var path = $"/updates/{update.UpdateId:D}/1";

        Assert.Contains("Approve for Test", await GetHtmlAsync(browser, path), StringComparison.Ordinal);
        using (var approved = await browser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "update", ["Action"] = $"approve:{RingEntity.TestId}" }))
        {
            Assert.Contains("The update was released for the ring.", await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Single((await scenario.ReleasesAsync(test)).Releases);

        using (await browser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "update", ["Action"] = "stop" }))
        {
        }

        Assert.Empty((await scenario.ReleasesAsync(test)).Releases);
        Assert.Contains("Rollout stopped", await GetHtmlAsync(browser, path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("approve:abc")]
    [InlineData("approve")]
    [InlineData("delete")]
    [InlineData("")]
    public async Task Operator_MalformedAction_IsRefused(string action)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Security Updates");
        await scenario.ReportAsync(pilot, update);
        using var browser = await LoginAsync(scenario, AdminRoles.Operator);

        using var response = await browser.PostFormAsync($"/updates/{update.UpdateId:D}/1", new Dictionary<string, string> { ["_handler"] = "update", ["Action"] = action });

        Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueuedAndUnknownUpdates_RenderSafely()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        await scenario.ManualStartOnlyAsync();
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var update = RolloutScenario.Update("Feature Packs");
        await scenario.ReportAsync(pilot, update);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        Assert.Contains("Queued: no rollout", await GetHtmlAsync(browser, $"/updates/{update.UpdateId:D}/1"), StringComparison.Ordinal);
        Assert.Contains("Update not found", await GetHtmlAsync(browser, $"/updates/{Guid.NewGuid():D}/1"), StringComparison.Ordinal);
    }

    private static async Task<DashboardClient> LoginAsync(RolloutScenario scenario, string role)
    {
        await scenario.Factory.CreateUserAsync("user", Password, role);
        var browser = new DashboardClient(scenario.Factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        return browser;
    }

    private static async Task<string> GetHtmlAsync(DashboardClient browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
