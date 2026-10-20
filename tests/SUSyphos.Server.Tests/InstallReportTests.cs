using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class InstallReportTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly Uri InstallUri = new(ApiRoutes.AgentInstallReport, UriKind.Relative);

    private static InstallReport Report(params InstallResult[] results) => new()
    {
        RunId = Guid.NewGuid(),
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        FinishedAt = DateTimeOffset.UtcNow,
        Results = results,
        RebootRequired = results.Any(r => r.RebootRequired),
        OsVersionAfter = "10.0.20348.2900",
    };

    private static InstallResult Result(Guid id, InstallResultCode code = InstallResultCode.Succeeded, int hresult = 0, bool reboot = true) => new()
    {
        UpdateId = id,
        Revision = 1,
        ResultCode = code,
        HResult = hresult,
        RebootRequired = reboot,
    };

    [Fact]
    public async Task ResultsForReleasedUpdates_AreStoredAsHistory()
    {
        using var client = factory.CreateClient();
        var (credentials, released) = await ApprovedClientWithReleaseAsync(client);

        using var response = await SendAsync(client, credentials, Report(Result(released)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var history = await factory.WithDbAsync(db => db.Installations.Where(i => i.ClientId == credentials.ClientId).ToListAsync(TestContext.Current.CancellationToken));
        var entry = Assert.Single(history);
        Assert.Equal((int)InstallResultCode.Succeeded, entry.ResultCode);
        Assert.Equal(InstallationService.CoordinatorSource, entry.Source);
        var stored = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.True(stored.RebootPending);
        Assert.Equal("10.0.20348.2900", stored.OsVersion);
        Assert.NotNull(stored.LastInstallAtUtc);
    }

    [Fact]
    public async Task ResultsForUpdatesNotReleased_AreIgnored()
    {
        using var client = factory.CreateClient();
        var (credentials, released) = await ApprovedClientWithReleaseAsync(client);
        var foreign = Guid.NewGuid();

        using var response = await SendAsync(client, credentials, Report(Result(released), Result(foreign)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var history = await factory.WithDbAsync(db => db.Installations.Where(i => i.ClientId == credentials.ClientId).Select(i => i.UpdateId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([released], history);
    }

    [Fact]
    public async Task SameRunSentTwice_IsStoredOnce()
    {
        using var client = factory.CreateClient();
        var (credentials, released) = await ApprovedClientWithReleaseAsync(client);
        var report = Report(Result(released));

        await SendAsync(client, credentials, report);
        using var second = await SendAsync(client, credentials, report);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var count = await factory.WithDbAsync(db => db.Installations.CountAsync(i => i.ClientId == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task FailedResult_IsStoredWithCode()
    {
        using var client = factory.CreateClient();
        var (credentials, released) = await ApprovedClientWithReleaseAsync(client);

        await SendAsync(client, credentials, Report(Result(released, InstallResultCode.Failed, unchecked((int)0x80070070), reboot: false)));

        var entry = await factory.WithDbAsync(db => db.Installations.SingleAsync(i => i.ClientId == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(unchecked((int)0x80070070), entry.HResult);
        Assert.Equal("There is not enough space on the disk.", HResultCatalog.Describe(entry.HResult));
    }

    [Fact]
    public async Task Report_FromPendingClient_IsForbidden()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await SendAsync(client, credentials, Report(Result(Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static readonly InstallReport[] InvalidReports =
    [
        Report(),
        Report(Result(Guid.NewGuid())) with { OsVersionAfter = "ten" },
        Report(Result(Guid.NewGuid()) with { Revision = -1 }),
        Report([.. Enumerable.Range(0, InstallReport.MaxResults + 1).Select(_ => Result(Guid.NewGuid()))]),
        Report(Result(Guid.NewGuid())) with { PostInstallChecks = [new HealthCheckResult { Name = "Bad Name", Status = HealthStatus.Ok }] },
    ];

    public static TheoryData<int> InvalidReportCases() => [.. Enumerable.Range(0, InvalidReports.Length)];

    [Theory]
    [MemberData(nameof(InvalidReportCases))]
    public async Task InvalidReport_IsRejected(int index)
    {
        using var client = factory.CreateClient();
        var (credentials, _) = await ApprovedClientWithReleaseAsync(client);

        using var response = await SendAsync(client, credentials, InvalidReports[index]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UndefinedResultCode_IsRejected()
    {
        using var client = factory.CreateClient();
        var (credentials, released) = await ApprovedClientWithReleaseAsync(client);
        var json = $"{{\"runId\":\"{Guid.NewGuid()}\",\"startedAt\":\"2026-10-18T00:00:00Z\",\"finishedAt\":\"2026-10-18T00:10:00Z\",\"results\":[{{\"updateId\":\"{released}\",\"revision\":1,\"resultCode\":17}}]}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, InstallUri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task GlobalPause_IsAnnouncedToApprovedClients(string setting, bool expected)
    {
        await using var paused = factory.WithWebHostBuilder(builder => builder.UseSetting("Installation:GlobalPause", setting));
        using var client = paused.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await ApproveAsync(paused.Services, credentials.ClientId);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0" }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var heartbeat = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(expected, heartbeat?.InstallationsPaused);
    }

    private static async Task ApproveAsync(IServiceProvider services, Guid clientId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        await db.Clients.Where(c => c.Id == clientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved), TestContext.Current.CancellationToken);
    }

    private async Task<(RegisterResponse Credentials, Guid Released)> ApprovedClientWithReleaseAsync(HttpClient client)
    {
        var credentials = await RegisterTests.RegisterAsync(client);
        var updateId = Guid.NewGuid();
        await factory.WithDbAsync(async db =>
        {
            await db.Clients.Where(c => c.Id == credentials.ClientId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved), TestContext.Current.CancellationToken);
            db.Updates.Add(new UpdateEntity { UpdateId = updateId, Revision = 1, Title = "Released update", FirstSeenAtUtc = DateTime.UtcNow });
            db.Releases.Add(new ReleaseEntity { ClientId = credentials.ClientId, UpdateId = updateId, Revision = 1, EarliestInstallAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        return (credentials, updateId);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, RegisterResponse credentials, InstallReport report)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, InstallUri) { Content = JsonContent.Create(report) };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

public sealed class HResultCatalogTests
{
    [Theory]
    [InlineData(0, "Success.")]
    [InlineData(unchecked((int)0x8024402C), "The proxy server or target server name cannot be resolved.")]
    [InlineData(unchecked((int)0x80070005), "Access is denied.")]
    public void KnownCodes_AreTranslated(int hresult, string expected)
    {
        Assert.Equal(expected, HResultCatalog.Describe(hresult));
    }

    [Theory]
    [InlineData(unchecked((int)0x8BADF00D), "Unknown error 0x8BADF00D.")]
    [InlineData(-1, "Unknown error 0xFFFFFFFF.")]
    public void UnknownCodes_AreShownAsHex(int hresult, string expected)
    {
        Assert.Equal(expected, HResultCatalog.Describe(hresult));
    }
}
