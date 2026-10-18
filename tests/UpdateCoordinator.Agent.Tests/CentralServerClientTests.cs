using System.Net;
using System.Text;

using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Tests;

/// <summary>
/// Responses of the central server are input too: the agent must reject malformed ones.
/// </summary>
public sealed class CentralServerClientTests
{
    private static readonly RegisterRequest Register = new()
    {
        HostName = "SRV01",
        OsVersion = "10.0.20348.2700",
        AgentVersion = "1.0.0",
    };

    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    [Theory]
    [InlineData("{\"clientId\":\"00000000-0000-0000-0000-000000000000\",\"clientSecret\":\"abc\"}")]
    [InlineData("{\"clientId\":\"6f1f6a5e-2a4b-4e0e-9a59-1c8e1d6c2f10\",\"clientSecret\":\"\"}")]
    [InlineData("null")]
    public async Task Register_WithInvalidResponse_ThrowsInvalidData(string json)
    {
        var client = CreateClient(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RegisterAsync(Register, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Register_WithOverlongSecret_ThrowsInvalidData()
    {
        var json = $"{{\"clientId\":\"{Guid.NewGuid()}\",\"clientSecret\":\"{new string('a', 65)}\"}}";
        var client = CreateClient(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RegisterAsync(Register, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"clientId\":\"not-a-guid\",\"clientSecret\":\"abc\"}")]
    public async Task Register_WithUnparsableResponse_Throws(string json)
    {
        var client = CreateClient(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.RegisterAsync(Register, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Register_WithErrorStatus_Throws(HttpStatusCode status)
    {
        var client = CreateClient(status, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync(Register, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"state\":\"Unknown\"}")]
    [InlineData("{\"state\":42}")]
    [InlineData("null")]
    public async Task Heartbeat_WithInvalidState_Throws(string json)
    {
        var client = CreateClient(HttpStatusCode.OK, json);

        await Assert.ThrowsAnyAsync<Exception>(() => client.HeartbeatAsync(Identity, new HeartbeatRequest { AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Heartbeat_WithUnauthorized_ReturnsNull()
    {
        var client = CreateClient(HttpStatusCode.Unauthorized, "");

        Assert.Null(await client.HeartbeatAsync(Identity, new HeartbeatRequest { AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Heartbeat_WithForbidden_Throws()
    {
        var client = CreateClient(HttpStatusCode.Forbidden, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.HeartbeatAsync(Identity, new HeartbeatRequest { AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken));
    }

    private static CentralServerClient CreateClient(HttpStatusCode status, string json) =>
        new(new HttpClient(new StaticResponseHandler(status, json)) { BaseAddress = new Uri("https://central.example.local/") });
}

/// <summary>Answers every request with the same status and JSON body.</summary>
internal sealed class StaticResponseHandler(HttpStatusCode status, string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
}

public sealed class ReleaseResponseTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    [Theory]
    [InlineData("{\"version\":-1,\"releases\":[]}")]
    [InlineData("{\"version\":1,\"releases\":null}")]
    [InlineData("{\"version\":1,\"releases\":[{\"updateId\":\"00000000-0000-0000-0000-000000000000\",\"revision\":1,\"earliestInstallAt\":\"2026-10-18T00:00:00Z\"}]}")]
    [InlineData("{\"version\":1,\"releases\":[{\"updateId\":\"6f1f6a5e-2a4b-4e0e-9a59-1c8e1d6c2f10\",\"revision\":-2,\"earliestInstallAt\":\"2026-10-18T00:00:00Z\"}]}")]
    [InlineData("null")]
    public async Task InvalidReleaseList_IsRejected(string json)
    {
        var client = Create(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetReleasesAsync(Identity, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("[{\"days\":[],\"start\":\"01:00:00\",\"end\":\"02:00:00\"}]")]
    [InlineData("[{\"days\":[9],\"start\":\"01:00:00\",\"end\":\"02:00:00\"}]")]
    [InlineData("[{\"days\":[1,1],\"start\":\"01:00:00\",\"end\":\"02:00:00\"}]")]
    [InlineData("[{\"days\":[1],\"start\":\"01:00:00\",\"end\":\"00:00:00\"}]")]
    [InlineData("[null]")]
    public async Task InvalidMaintenanceWindows_AreRejected(string windows)
    {
        var client = Create(HttpStatusCode.OK, $"{{\"version\":1,\"releases\":[],\"maintenanceWindows\":{windows}}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetReleasesAsync(Identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedWindowTime_IsRejected()
    {
        var client = Create(HttpStatusCode.OK, "{\"version\":1,\"releases\":[],\"maintenanceWindows\":[{\"days\":[1],\"start\":\"25:00:00\",\"end\":\"02:00:00\"}]}");

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.GetReleasesAsync(Identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TooManyMaintenanceWindows_AreRejected()
    {
        var windows = string.Join(',', Enumerable.Repeat("{\"days\":[1],\"start\":\"01:00:00\",\"end\":\"02:00:00\"}", MaintenanceWindow.MaxWindows + 1));
        var client = Create(HttpStatusCode.OK, $"{{\"version\":1,\"releases\":[],\"maintenanceWindows\":[{windows}]}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetReleasesAsync(Identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidMaintenanceWindows_AreAccepted()
    {
        var client = Create(HttpStatusCode.OK, "{\"version\":1,\"releases\":[],\"maintenanceWindows\":[{\"days\":[0,6],\"start\":\"00:00:00\",\"end\":\"1.00:00:00\"}]}");

        var result = await client.GetReleasesAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], Assert.Single(result!.MaintenanceWindows!).Days);
    }

    [Fact]
    public async Task TooManyReleases_AreRejected()
    {
        var items = string.Join(',', Enumerable.Range(0, 5_001).Select(_ => $"{{\"updateId\":\"{Guid.NewGuid()}\",\"revision\":1,\"earliestInstallAt\":\"2026-10-18T00:00:00Z\"}}"));
        var client = Create(HttpStatusCode.OK, $"{{\"version\":1,\"releases\":[{items}]}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetReleasesAsync(Identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unauthorized_ReturnsNull()
    {
        Assert.Null(await Create(HttpStatusCode.Unauthorized, "").GetReleasesAsync(Identity, TestContext.Current.CancellationToken));
    }

    private static CentralServerClient Create(HttpStatusCode status, string json) =>
        new(new HttpClient(new StaticResponseHandler(status, json)) { BaseAddress = new Uri("https://central.example.local/") });
}
