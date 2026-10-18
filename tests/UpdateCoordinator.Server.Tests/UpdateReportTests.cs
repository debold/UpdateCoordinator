using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class UpdateReportTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly Uri ReportUri = new(ApiRoutes.AgentUpdateReport, UriKind.Relative);
    private static readonly Uri ReleasesUri = new(ApiRoutes.AgentReleases, UriKind.Relative);

    internal static AvailableUpdate Available(Guid? id = null) => new()
    {
        UpdateId = id ?? Guid.NewGuid(),
        Revision = 1,
        KbArticle = "5034129",
        Title = "2024-01 Cumulative Update",
        Classification = "Security Updates",
        MaxDownloadSizeBytes = 1_234_567,
        RebootBehavior = RebootBehavior.AlwaysRequiresReboot,
        MsrcSeverity = "Critical",
    };

    internal static UpdateReport SuccessfulReport(params AvailableUpdate[] updates) => new()
    {
        StartedAt = DateTimeOffset.UtcNow,
        SearchSucceeded = true,
        HealthChecks = [new HealthCheckResult { Name = "DiskSpace", Status = HealthStatus.Ok, Detail = "Free: 50000 MB" }],
        AvailableUpdates = updates,
    };

    [Fact]
    public async Task Report_FromApprovedClient_IsStored()
    {
        using var client = factory.CreateClient();
        var credentials = await ApprovedClientAsync(client);
        var update = Available();

        using var response = await SendAsync(client, credentials, SuccessfulReport(update));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await GetClientAsync(credentials.ClientId);
        Assert.True(stored.LastSearchSucceeded);
        Assert.NotNull(stored.LastSearchAtUtc);
        Assert.Equal("DiskSpace", Assert.Single(stored.HealthChecks).Name);
        var available = await factory.WithDbAsync(db => db.ClientAvailableUpdates.Where(a => a.ClientId == credentials.ClientId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(update.UpdateId, Assert.Single(available).UpdateId);
        var catalog = await factory.WithDbAsync(db => db.Updates.SingleAsync(u => u.UpdateId == update.UpdateId, TestContext.Current.CancellationToken));
        Assert.Equal(1_234_567, catalog.MaxDownloadSizeBytes);
        Assert.Equal("Critical", catalog.MsrcSeverity);
    }

    [Fact]
    public async Task FailedSearch_KeepsPreviousListAndStoresErrorCode()
    {
        using var client = factory.CreateClient();
        var credentials = await ApprovedClientAsync(client);
        var update = Available();
        await SendAsync(client, credentials, SuccessfulReport(update));

        using var response = await SendAsync(client, credentials, new UpdateReport
        {
            StartedAt = DateTimeOffset.UtcNow,
            SearchHResult = unchecked((int)0x8024402C),
            HealthChecks = [new HealthCheckResult { Name = "UpdateService", Status = HealthStatus.Failed, Detail = "disabled" }],
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await GetClientAsync(credentials.ClientId);
        Assert.False(stored.LastSearchSucceeded);
        Assert.Equal(unchecked((int)0x8024402C), stored.LastSearchHResult);
        var available = await factory.WithDbAsync(db => db.ClientAvailableUpdates.Where(a => a.ClientId == credentials.ClientId).CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, available);
    }

    [Fact]
    public async Task NewReport_ReplacesAvailableUpdates()
    {
        using var client = factory.CreateClient();
        var credentials = await ApprovedClientAsync(client);
        await SendAsync(client, credentials, SuccessfulReport(Available(), Available()));

        await SendAsync(client, credentials, SuccessfulReport());

        var available = await factory.WithDbAsync(db => db.ClientAvailableUpdates.Where(a => a.ClientId == credentials.ClientId).CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, available);
    }

    [Fact]
    public async Task Report_FromPendingClient_IsForbidden()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await SendAsync(client, credentials, SuccessfulReport());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static readonly UpdateReport[] InvalidReports =
    [
        SuccessfulReport() with { HealthChecks = [new HealthCheckResult { Name = "Disk Space", Status = HealthStatus.Ok }] },
        SuccessfulReport() with { HealthChecks = [new HealthCheckResult { Name = "Disk", Status = HealthStatus.Ok, Detail = "line\nbreak" }] },
        SuccessfulReport() with { HealthChecks = [.. Enumerable.Range(0, UpdateReport.MaxHealthChecks + 1).Select(i => new HealthCheckResult { Name = $"Check{i}", Status = HealthStatus.Ok })] },
        SuccessfulReport(Available() with { MaxDownloadSizeBytes = -1 }),
        SuccessfulReport(Available() with { MsrcSeverity = "Very critical" }),
        SuccessfulReport(Available() with { Title = "" }),
        SuccessfulReport(Available() with { KbArticle = "KB1" }),
        SuccessfulReport() with { AvailableUpdates = [.. Enumerable.Range(0, UpdateReport.MaxAvailableUpdates + 1).Select(_ => Available())] },
    ];

    public static TheoryData<int> InvalidReportCases() => [.. Enumerable.Range(0, InvalidReports.Length)];

    [Theory]
    [MemberData(nameof(InvalidReportCases))]
    public async Task InvalidReport_IsRejected(int index)
    {
        using var client = factory.CreateClient();
        var credentials = await ApprovedClientAsync(client);

        using var response = await SendAsync(client, credentials, InvalidReports[index]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"startedAt\":\"2026-10-18T00:00:00Z\",\"healthChecks\":[{\"name\":\"Disk\",\"status\":\"Unknown\"}]}")]
    [InlineData("{\"startedAt\":\"2026-10-18T00:00:00Z\",\"healthChecks\":[{\"name\":\"Disk\",\"status\":42}]}")]
    [InlineData("{\"startedAt\":\"yesterday\"}")]
    [InlineData("{\"startedAt\":\"2026-10-18T00:00:00Z\",\"searchSucceeded\":true,\"availableUpdates\":[{\"updateId\":\"6f1f6a5e-2a4b-4e0e-9a59-1c8e1d6c2f10\",\"revision\":1,\"title\":\"T\",\"rebootBehavior\":9}]}")]
    public async Task UnparsableReport_IsRejected(string json)
    {
        using var client = factory.CreateClient();
        var credentials = await ApprovedClientAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, ReportUri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Releases_AreDeliveredWithVersionInHeartbeatAndList()
    {
        using var client = factory.CreateClient();
        var credentials = await ApprovedClientAsync(client);
        var update = Available();
        await SendAsync(client, credentials, SuccessfulReport(update));
        var earliest = new DateTime(2026, 10, 20, 19, 0, 0, DateTimeKind.Utc);
        await factory.WithDbAsync(async db =>
        {
            db.Releases.Add(new ReleaseEntity { ClientId = credentials.ClientId, UpdateId = update.UpdateId, Revision = update.Revision, EarliestInstallAtUtc = earliest, CreatedAtUtc = DateTime.UtcNow });
            var entity = await db.Clients.SingleAsync(c => c.Id == credentials.ClientId);
            entity.ReleasesVersion = 5;
            return await db.SaveChangesAsync();
        });

        var heartbeat = await HeartbeatAsync(client, credentials);
        var releases = await GetReleasesAsync(client, credentials);

        Assert.Equal(5, heartbeat.ReleasesVersion);
        Assert.Equal(5, releases.Version);
        var release = Assert.Single(releases.Releases);
        Assert.Equal(update.UpdateId, release.UpdateId);
        Assert.Equal(new DateTimeOffset(earliest), release.EarliestInstallAt);
    }

    [Fact]
    public async Task PendingClient_GetsNoReleaseInformation()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        var heartbeat = await HeartbeatAsync(client, credentials);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUri);
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        using var releases = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Null(heartbeat.ReleasesVersion);
        Assert.Equal(HttpStatusCode.Forbidden, releases.StatusCode);
    }

    [Fact]
    public async Task Releases_WithoutCredentials_AreUnauthorized()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ReleasesUri, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<RegisterResponse> ApprovedClientAsync(HttpClient client)
    {
        var credentials = await RegisterTests.RegisterAsync(client);
        await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved), TestContext.Current.CancellationToken));
        return credentials;
    }

    private Task<ClientEntity> GetClientAsync(Guid clientId) =>
        factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == clientId, TestContext.Current.CancellationToken));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, RegisterResponse credentials, UpdateReport report)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ReportUri) { Content = JsonContent.Create(report) };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HeartbeatResponse> HeartbeatAsync(HttpClient client, RegisterResponse credentials)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0" }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<ReleaseList> GetReleasesAsync(HttpClient client, RegisterResponse credentials)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUri);
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReleaseList>(TestContext.Current.CancellationToken))!;
    }
}
