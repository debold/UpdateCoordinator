using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using UpdateCoordinator.Contracts;

namespace UpdateCoordinator.Server.Tests;

public sealed class HostingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string OpenApiDocument = "/openapi/v1.json";

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ApiRoutes.Health, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownRoute_ReturnsNotFound()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_IsAvailableInDevelopment()
    {
        using var client = CreateClient("Development");

        using var response = await client.GetAsync(new Uri(OpenApiDocument, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_IsNotExposedInProduction()
    {
        using var client = CreateClient("Production");

        using var response = await client.GetAsync(new Uri(OpenApiDocument, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient CreateClient(string environment) =>
        factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment)).CreateClient();
}
