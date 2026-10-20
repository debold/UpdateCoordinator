using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class RegisterTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly Uri RegisterUri = new(ApiRoutes.AgentRegister, UriKind.Relative);

    internal static RegisterRequest ValidRequest(string hostName = "SRV01") => new()
    {
        HostName = hostName,
        DomainName = "example.local",
        OsVersion = "10.0.20348.2700",
        AgentVersion = "1.2.3-alpha.0.4+abc123",
    };

    [Fact]
    public async Task Register_WithValidRequest_ReturnsCredentialsAndStoresPendingClient()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(RegisterUri, ValidRequest("SRV-REGISTER"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.ClientId);
        Assert.True(body.ClientSecret.Length >= 43);

        var stored = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == body.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(RegistrationState.Pending, stored.State);
        Assert.Equal("SRV-REGISTER", stored.HostName);
        Assert.Equal(32, stored.SecretHash.Length);
        Assert.NotEqual(body.ClientSecret, Convert.ToBase64String(stored.SecretHash));
    }

    [Fact]
    public async Task Register_Twice_IssuesDifferentCredentials()
    {
        using var client = factory.CreateClient();

        var first = await RegisterAsync(client);
        var second = await RegisterAsync(client);

        Assert.NotEqual(first.ClientId, second.ClientId);
        Assert.NotEqual(first.ClientSecret, second.ClientSecret);
    }

    public static TheoryData<RegisterRequest> InvalidRequests() => new(
        ValidRequest() with { HostName = "" },
        ValidRequest() with { HostName = "srv_01" },
        ValidRequest() with { HostName = "-srv" },
        ValidRequest() with { HostName = "srv'; DROP TABLE Clients;--" },
        ValidRequest() with { HostName = new string('a', 64) },
        ValidRequest() with { DomainName = "example..local" },
        ValidRequest() with { DomainName = "<script>.local" },
        ValidRequest() with { OsVersion = "10" },
        ValidRequest() with { OsVersion = "Windows Server 2022" },
        ValidRequest() with { AgentVersion = "1.2" },
        ValidRequest() with { AgentVersion = "v1.2.3" });

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Register_WithInvalidRequest_ReturnsBadRequest(RegisterRequest request)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(RegisterUri, request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task Register_WithMalformedBody_ReturnsBadRequest(string body)
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(RegisterUri, content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WhenAlreadyRegistered_IsForbidden()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, RegisterUri) { Content = JsonContent.Create(ValidRequest()) };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    internal static async Task<RegisterResponse> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(RegisterUri, ValidRequest(), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>(TestContext.Current.CancellationToken))!;
    }
}

public sealed class RegisterRateLimitTests(RateLimitedServerFactory factory) : IClassFixture<RateLimitedServerFactory>
{
    [Fact]
    public async Task Register_AboveLimit_ReturnsTooManyRequests()
    {
        using var client = factory.CreateClient();
        var uri = new Uri(ApiRoutes.AgentRegister, UriKind.Relative);

        for (var i = 0; i < RateLimitedServerFactory.Limit; i++)
        {
            using var accepted = await client.PostAsJsonAsync(uri, RegisterTests.ValidRequest(), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var rejected = await client.PostAsJsonAsync(uri, RegisterTests.ValidRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }
}
