using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent;
using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.IntegrationTests;

/// <summary>
/// Inventory with the real Windows sources of this machine (registry, WUA API, domain) against the central server.
/// </summary>
public sealed class InventoryIntegrationTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task WuaApi_ReturnsInstalledUpdatesWithValidIdentity()
    {
        var source = new WuaInstalledUpdateSource(NullLogger<WuaInstalledUpdateSource>.Instance);

        var updates = await source.GetInstalledUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.SkipWhen(updates is null, "The Windows Update Agent API is not available on this machine.");
        Assert.All(updates!, update =>
        {
            Assert.True(Guid.TryParse(update.UpdateId, out _), $"Invalid update ID {update.UpdateId}");
            Assert.True(update.Revision >= 0);
            Assert.False(string.IsNullOrWhiteSpace(update.Title));
        });
    }

    [Fact]
    public async Task RealInventory_IsAcceptedByCentralServer()
    {
        var probe = new WindowsSystemProbe();
        var options = Options.Create(new InventoryOptions());
        var collector = new InventoryCollector(
            probe,
            new ComputerSidReader(NullLogger<ComputerSidReader>.Instance),
            new WuaInstalledUpdateSource(NullLogger<WuaInstalledUpdateSource>.Instance),
            new RoleDetector(probe, options),
            new AgentEnvironment(probe));
        var inventory = await collector.CollectAsync(TestContext.Current.CancellationToken);
        var client = new CentralServerClient(factory.CreateClient());
        var identity = await client.RegisterAsync(
            new RegisterRequest { HostName = inventory.HostName, OsVersion = inventory.OperatingSystem.Version, AgentVersion = "1.0.0" },
            TestContext.Current.CancellationToken);

        var accepted = await client.SendInventoryAsync(identity, inventory, TestContext.Current.CancellationToken);

        Assert.True(accepted);
    }
}

public sealed class InventorySizeLimitTests : IAsyncDisposable
{
    private readonly KestrelServerFactory _server = new();

    [Fact]
    public async Task OversizedInventory_IsRejectedBeforeProcessing()
    {
        using var handler = new HttpClientHandler
        {
            // Test-only: the certificate is not under test here, the request size limit is.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var http = new HttpClient(handler) { BaseAddress = _server.BaseAddress };
        var credentials = await new CentralServerClient(http).RegisterAsync(
            new RegisterRequest { HostName = "SRV-BIG", OsVersion = "10.0.20348.1", AgentVersion = "1.0.0" },
            TestContext.Current.CancellationToken);

        var oversized = new string(' ', 5 * 1024 * 1024) + "{}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentInventory, UriKind.Relative))
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);

        // Kestrel answers 413 based on Content-Length and may close the connection while the client is
        // still uploading; both outcomes prove the body was refused before it was processed.
        try
        {
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException)
        {
            // Connection closed by the server after rejecting the request.
        }
    }

    [Fact]
    public async Task LargeButValidInventory_IsAccepted()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var http = new HttpClient(handler) { BaseAddress = _server.BaseAddress };
        var client = new CentralServerClient(http);
        var credentials = await client.RegisterAsync(
            new RegisterRequest { HostName = "SRV-MANY", OsVersion = "10.0.20348.1", AgentVersion = "1.0.0" },
            TestContext.Current.CancellationToken);
        var updates = Enumerable.Range(0, InventoryRequest.MaxInstalledUpdates)
            .Select(i => new InstalledUpdate { UpdateId = Guid.NewGuid(), Revision = 1, KbArticle = "5034129", Title = $"Update {i} " + new string('x', 200), Classification = "Security Updates" })
            .ToList();
        var inventory = new InventoryRequest
        {
            HostName = "SRV-MANY",
            OperatingSystem = new OperatingSystemInfo { ProductName = "Windows Server", Version = "10.0.20348.1" },
            UpdateSource = new UpdateSourceInfo(),
            InstalledUpdates = updates,
        };

        Assert.True(await client.SendInventoryAsync(new(credentials.ClientId, credentials.ClientSecret), inventory, TestContext.Current.CancellationToken));
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
