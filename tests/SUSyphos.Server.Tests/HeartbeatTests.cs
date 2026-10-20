using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class HeartbeatTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly Uri HeartbeatUri = new(ApiRoutes.AgentHeartbeat, UriKind.Relative);
    private static readonly HeartbeatRequest ValidRequest = new() { AgentVersion = "1.2.4" };

    [Fact]
    public async Task Heartbeat_WithValidCredentials_ReturnsStateAndUpdatesClient()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await SendAsync(client, AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret), ValidRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(RegistrationState.Pending, body?.State);

        var stored = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.NotNull(stored.LastHeartbeatAtUtc);
        Assert.Equal("1.2.4", stored.AgentVersion);
    }

    [Fact]
    public async Task Heartbeat_ForApprovedClient_ReturnsApproved()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await SetStateAsync(credentials.ClientId, RegistrationState.Approved);

        using var response = await SendAsync(client, AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret), ValidRequest);

        var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(RegistrationState.Approved, body?.State);
    }

    [Fact]
    public async Task Heartbeat_WithoutCredentials_IsUnauthorized()
    {
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, null, ValidRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_WithWrongSecret_IsUnauthorized()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await SendAsync(client, AgentCredentials.ToHeader(credentials.ClientId, "wrong-secret"), ValidRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_WithUnknownClient_IsUnauthorized()
    {
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, AgentCredentials.ToHeader(Guid.NewGuid(), "any-secret"), ValidRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Basic", "not-base64!")]
    [InlineData("Basic", "bm9jb2xvbg==")]
    [InlineData("Bearer", "token")]
    public async Task Heartbeat_WithMalformedCredentials_IsUnauthorized(string scheme, string parameter)
    {
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, new AuthenticationHeaderValue(scheme, parameter), ValidRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(RegistrationState.Rejected)]
    [InlineData(RegistrationState.Blocked)]
    [InlineData(RegistrationState.Deleted)]
    public async Task Heartbeat_InStateWithoutPermission_IsForbidden(RegistrationState state)
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await SetStateAsync(credentials.ClientId, state);

        using var response = await SendAsync(client, AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret), ValidRequest);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_WithInvalidBody_ReturnsBadRequest()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await SendAsync(
            client,
            AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret),
            new HeartbeatRequest { AgentVersion = "not-a-version" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<int> SetStateAsync(Guid clientId, RegistrationState state) =>
        factory.WithDbAsync(db => db.Clients
            .Where(c => c.Id == clientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, state), TestContext.Current.CancellationToken));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, AuthenticationHeaderValue? authorization, HeartbeatRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, HeartbeatUri) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = authorization;
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
