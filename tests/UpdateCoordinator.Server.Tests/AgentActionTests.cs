using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Agents;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class AgentActionTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task QueuedAction_IsAnnouncedFetchedOnceAndConfirmed()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        using var browser = await LoginAsync(scenario, AdminRoles.Operator);

        using (var queued = await browser.PostFormAsync($"/servers/{client.ClientId}", new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = "queue:SearchNow" }))
        {
            Assert.Contains("The action was queued", await queued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(1, (await HeartbeatAsync(scenario, client)).PendingActions);
        var action = Assert.Single((await FetchAsync(scenario, client)).Actions);
        Assert.Equal(AgentActionKind.SearchNow, action.Kind);
        Assert.Equal(0, (await HeartbeatAsync(scenario, client)).PendingActions);
        Assert.Empty((await FetchAsync(scenario, client)).Actions);

        Assert.Equal(HttpStatusCode.NoContent, await ResultAsync(scenario, client, action.Id, new AgentActionResult { Status = AgentActionStatus.Succeeded, Detail = "Search\u0007 started" }));
        Assert.Equal(HttpStatusCode.NotFound, await ResultAsync(scenario, client, action.Id, new AgentActionResult { Status = AgentActionStatus.Failed }));

        var stored = await scenario.Factory.WithDbAsync(db => db.AgentActions.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentActionState.Succeeded, stored.State);
        Assert.Equal("Search started", stored.Detail);
        Assert.Contains("Search started", await GetHtmlAsync(browser, $"/servers/{client.ClientId}"), StringComparison.Ordinal);
        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Action.Queued", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Actions_AreIsolatedPerClient()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var first = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var second = await scenario.AddClientAsync("SRV02", RingEntity.PilotId);
        await QueueAsync(scenario, first.ClientId, AgentActionKind.FullResync);

        Assert.Empty((await FetchAsync(scenario, second)).Actions);
        var action = Assert.Single((await FetchAsync(scenario, first)).Actions);
        Assert.Equal(HttpStatusCode.NotFound, await ResultAsync(scenario, second, action.Id, new AgentActionResult { Status = AgentActionStatus.Succeeded }));
    }

    [Fact]
    public async Task InvalidRequests_AreRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        var pending = await RegisterTests.RegisterAsync(scenario.Http);

        Assert.False((await QueueAsync(scenario, pending.ClientId, AgentActionKind.SearchNow, expectSuccess: false)).Succeeded);
        Assert.False((await QueueAsync(scenario, Guid.NewGuid(), AgentActionKind.SearchNow, expectSuccess: false)).Succeeded);
        Assert.False((await QueueAsync(scenario, client.ClientId, (AgentActionKind)42, expectSuccess: false)).Succeeded);
        for (var i = 0; i < AgentActionService.MaxOpenPerClient; i++)
        {
            await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow);
        }

        Assert.False((await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow, expectSuccess: false)).Succeeded);

        // A pending client may not fetch actions at all.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentActions, UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(pending.ClientId, pending.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"status\":\"Succeeded\",\"detail\":\"" + "x" + "\"}", HttpStatusCode.NoContent)]
    [InlineData("{\"status\":\"Exploded\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"status\":7}", HttpStatusCode.BadRequest)]
    [InlineData("not json", HttpStatusCode.BadRequest)]
    public async Task ResultBody_IsValidated(string body, HttpStatusCode expected)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow);
        var action = Assert.Single((await FetchAsync(scenario, client)).Actions);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentActionResultFor(action.Id), UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task TooLongDetail_IsRejected()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow);
        var action = Assert.Single((await FetchAsync(scenario, client)).Actions);

        Assert.Equal(HttpStatusCode.BadRequest, await ResultAsync(scenario, client, action.Id, new AgentActionResult { Status = AgentActionStatus.Succeeded, Detail = new string('a', 513) }));
    }

    [Fact]
    public async Task OnlyQueuedActionsCanBeCancelled()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow);
        var queued = await scenario.Factory.WithDbAsync(db => db.AgentActions.Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken));

        Assert.True((await scenario.WithServiceAsync<AgentActionService, Outcome>(s => s.CancelAsync(client.ClientId, queued, "operator", CancellationToken.None))).Succeeded);
        Assert.Empty((await FetchAsync(scenario, client)).Actions);

        await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow);
        var delivered = Assert.Single((await FetchAsync(scenario, client)).Actions);
        Assert.False((await scenario.WithServiceAsync<AgentActionService, Outcome>(s => s.CancelAsync(client.ClientId, delivered.Id, "operator", CancellationToken.None))).Succeeded);
    }

    [Fact]
    public async Task StaleActions_Expire()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.FullResync);
        await scenario.Factory.WithDbAsync(db => db.AgentActions.Where(a => a.Kind == AgentActionKind.SearchNow)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.RequestedAtUtc, DateTime.UtcNow.AddHours(-25)), TestContext.Current.CancellationToken));
        await scenario.Factory.WithDbAsync(db => db.AgentActions.Where(a => a.Kind == AgentActionKind.FullResync)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AgentActionState.Delivered).SetProperty(a => a.DeliveredAtUtc, DateTime.UtcNow.AddHours(-2)), TestContext.Current.CancellationToken));

        Assert.Equal(2, await scenario.WithServiceAsync<AgentActionService, int>(s => s.ExpireAsync(CancellationToken.None)));
        Assert.All(await scenario.Factory.WithDbAsync(db => db.AgentActions.ToListAsync(TestContext.Current.CancellationToken)), a => Assert.Equal(AgentActionState.Expired, a.State));
    }

    [Theory]
    [InlineData("queue:1")]
    [InlineData("queue:Bogus")]
    [InlineData("queue:searchnow")]
    [InlineData("cancel:not-a-guid")]
    [InlineData("reboot")]
    public async Task MalformedFormValues_AreRefused(string value)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        using var browser = await LoginAsync(scenario, AdminRoles.Operator);

        using var response = await browser.PostFormAsync($"/servers/{client.ClientId}", new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = value });

        Assert.Contains("Invalid request.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Reader_CannotQueueActions()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        var html = await GetHtmlAsync(browser, $"/servers/{client.ClientId}");
        using (await browser.PostFormAsync($"/servers/{client.ClientId}", new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = "queue:RestartService" }, pageToRead: "/"))
        {
        }

        Assert.DoesNotContain("Restart agent service", html, StringComparison.Ordinal);
        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task UploadedLogs_CanBeDownloadedByOperators_AndExpire()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.UploadLogs);
        var action = Assert.Single((await FetchAsync(scenario, client)).Actions);
        var archive = Zip("agent-20261019.log", "log line");

        Assert.Equal(HttpStatusCode.NoContent, await UploadAsync(scenario, client, action.Id, archive));
        Assert.Equal(archive.Length, await scenario.Factory.WithDbAsync(db => db.AgentActions.Select(a => a.LogFileSize).SingleAsync(TestContext.Current.CancellationToken)));

        using var browser = await LoginAsync(scenario, AdminRoles.Operator);
        Assert.Contains(AgentLogEndpoints.DownloadPath(action.Id), await GetHtmlAsync(browser, $"/servers/{client.ClientId}"), StringComparison.Ordinal);
        using (var download = await browser.GetAsync(AgentLogEndpoints.DownloadPath(action.Id)))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(archive, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        }

        Assert.True(await scenario.Factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Action.LogsDownloaded", TestContext.Current.CancellationToken)));

        await scenario.Factory.WithDbAsync(db => db.AgentActions.ExecuteUpdateAsync(s => s.SetProperty(a => a.RequestedAtUtc, DateTime.UtcNow.AddDays(-15)), TestContext.Current.CancellationToken));
        Assert.Equal(1, await scenario.WithServiceAsync<AgentActionService, int>(s => s.DeleteOldLogsAsync(CancellationToken.None)));
        using var gone = await browser.GetAsync(AgentLogEndpoints.DownloadPath(action.Id));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task LogDownload_IsForOperatorsOnly()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        using var browser = await LoginAsync(scenario, AdminRoles.Reader);

        using var response = await browser.GetAsync(AgentLogEndpoints.DownloadPath(Guid.NewGuid()));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidLogUploads_AreRefused()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.SearchNow);
        await QueueAsync(scenario, client.ClientId, AgentActionKind.UploadLogs);
        var actions = (await FetchAsync(scenario, client)).Actions;
        var search = actions.Single(a => a.Kind == AgentActionKind.SearchNow);
        var logs = actions.Single(a => a.Kind == AgentActionKind.UploadLogs);

        Assert.Equal(HttpStatusCode.NotFound, await UploadAsync(scenario, client, search.Id, Zip("a.log", "x")));
        Assert.Equal(HttpStatusCode.NotFound, await UploadAsync(scenario, client, Guid.NewGuid(), Zip("a.log", "x")));
        Assert.Equal(HttpStatusCode.BadRequest, await UploadAsync(scenario, client, logs.Id, System.Text.Encoding.UTF8.GetBytes("not a zip archive at all, sorry")));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadAsync(scenario, client, logs.Id, Zip("a.log", "x"), "text/plain"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await UploadAsync(scenario, client, logs.Id, new byte[AgentActionList.MaxLogBytes + 1]));
        Assert.Null(await scenario.Factory.WithDbAsync(db => db.AgentActions.Where(a => a.Id == logs.Id).Select(a => a.LogFileSize).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ServerRestart_NeedsAdministratorAndConfirmation()
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);
        await scenario.Factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        await scenario.Factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        var path = $"/servers/{client.ClientId}";

        using (var operatorBrowser = new DashboardClient(scenario.Factory))
        {
            using (await operatorBrowser.LoginAsync("operator", Password))
            {
            }

            Assert.DoesNotContain("value=\"queue:RestartServer\"", await GetHtmlAsync(operatorBrowser, path), StringComparison.Ordinal);
            using var refused = await operatorBrowser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = "queue:RestartServer", ["ConfirmRestart"] = "yes" });
            Assert.Contains("Only administrators can restart a server.", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using var adminBrowser = new DashboardClient(scenario.Factory);
        using (await adminBrowser.LoginAsync("admin", Password))
        {
        }

        using (var unconfirmed = await adminBrowser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = "queue:RestartServer" }))
        {
            Assert.Contains("Confirm the restart", await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.False(await scenario.Factory.WithDbAsync(db => db.AgentActions.AnyAsync(TestContext.Current.CancellationToken)));

        using (await adminBrowser.PostFormAsync(path, new Dictionary<string, string> { ["_handler"] = "action", ["RemoteAction"] = "queue:RestartServer", ["ConfirmRestart"] = "yes" }))
        {
        }

        Assert.Equal(AgentActionKind.RestartServer, await scenario.Factory.WithDbAsync(db => db.AgentActions.Select(a => a.Kind).SingleAsync(TestContext.Current.CancellationToken)));
    }

    private static byte[] Zip(string name, string content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private static async Task<HttpStatusCode> UploadAsync(RolloutScenario scenario, RegisterResponse client, Guid actionId, byte[] archive, string contentType = "application/zip")
    {
        using var content = new ByteArrayContent(archive);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentActionLogsFor(actionId), UriKind.Relative)) { Content = content };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<Outcome> QueueAsync(RolloutScenario scenario, Guid clientId, AgentActionKind kind, bool expectSuccess = true)
    {
        var outcome = await scenario.WithServiceAsync<AgentActionService, Outcome>(s => s.QueueAsync(clientId, kind, "operator", CancellationToken.None));
        if (expectSuccess)
        {
            Assert.True(outcome.Succeeded, outcome.Error);
        }

        return outcome;
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

    private static async Task<AgentActionList> FetchAsync(RolloutScenario scenario, RegisterResponse client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentActions, UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentActionList>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<HttpStatusCode> ResultAsync(RolloutScenario scenario, RegisterResponse client, Guid actionId, AgentActionResult result)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentActionResultFor(actionId), UriKind.Relative)) { Content = JsonContent.Create(result) };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
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

public sealed class TimeZoneTests
{
    [Theory]
    [InlineData("UTC", "UTC")]
    [InlineData("W. Europe Standard Time", "W. Europe Standard Time")]
    [InlineData("Mars Standard Time", null)]
    public async Task Inventory_StoresOnlyKnownTimeZones(string zone, string? expected)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        using var response = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory() with { TimeZoneId = zone });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(expected, await scenario.Factory.WithDbAsync(db => db.Clients.Select(c => c.TimeZoneId).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("<script>")]
    [InlineData("../../etc")]
    public async Task Inventory_MalformedTimeZone_IsRejected(string zone)
    {
        await using var scenario = await RolloutScenario.CreateAsync();
        var client = await scenario.AddClientAsync("SRV01", RingEntity.PilotId);

        using var response = await InventoryTests.SendAsync(scenario.Http, client, InventoryTests.ValidInventory() with { TimeZoneId = zone });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void NextOpening_FindsTheNextStartOrNow()
    {
        var windows = MaintenanceWindow.SystemDefault;

        Assert.Equal(new DateTime(2026, 10, 19, 19, 0, 0), MaintenanceSchedule.NextOpening(windows, new DateTime(2026, 10, 19, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 24, 1, 0, 0), MaintenanceSchedule.NextOpening(windows, new DateTime(2026, 10, 24, 1, 0, 0)));
        Assert.Null(MaintenanceSchedule.NextOpening([], new DateTime(2026, 10, 19, 12, 0, 0)));
    }
}
