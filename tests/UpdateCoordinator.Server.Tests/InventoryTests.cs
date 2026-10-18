using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

public sealed class InventoryTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly Uri InventoryUri = new(ApiRoutes.AgentInventory, UriKind.Relative);
    private static readonly Uri HeartbeatUri = new(ApiRoutes.AgentHeartbeat, UriKind.Relative);

    internal static InventoryRequest ValidInventory(string host = "SRV01", params InstalledUpdate[] updates) => new()
    {
        HostName = host,
        DomainName = "example.local",
        OperatingSystem = new OperatingSystemInfo
        {
            ProductName = "Windows Server 2022 Standard",
            EditionId = "ServerStandard",
            DisplayVersion = "21H2",
            Version = "10.0.20348.2700",
        },
        Roles = ["DomainController", "Custom.Backup"],
        UpdateSource = new UpdateSourceInfo(),
        InstalledUpdates = updates,
    };

    internal static InstalledUpdate Update(Guid? id = null, int revision = 1, string title = "2024-01 Cumulative Update") => new()
    {
        UpdateId = id ?? Guid.NewGuid(),
        Revision = revision,
        KbArticle = "5034129",
        Title = title,
        Classification = "Security Updates",
    };

    [Fact]
    public async Task Inventory_IsStoredWithUpdatesAndCatalog()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        var update = Update();

        using var response = await SendAsync(client, credentials, ValidInventory("SRV-INV", update));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal("SRV-INV", stored.HostName);
        Assert.Equal("Windows Server 2022 Standard", stored.OsProductName);
        Assert.Equal("10.0.20348.2700", stored.OsVersion);
        Assert.Equal(["DomainController", "Custom.Backup"], stored.Roles);
        Assert.NotNull(stored.InventoryAtUtc);

        var installed = await factory.WithDbAsync(db => db.ClientUpdates.Where(cu => cu.ClientId == credentials.ClientId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(update.UpdateId, Assert.Single(installed).UpdateId);
        var catalog = await factory.WithDbAsync(db => db.Updates.SingleAsync(u => u.UpdateId == update.UpdateId, TestContext.Current.CancellationToken));
        Assert.Equal("5034129", catalog.KbArticle);
    }

    [Fact]
    public async Task Inventory_IsAcceptedBeforeApproval()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await SendAsync(client, credentials, ValidInventory());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task NewInventory_ReplacesInstalledUpdates()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        var first = Update();
        var second = Update();
        await SendAsync(client, credentials, ValidInventory("SRV01", first));

        await SendAsync(client, credentials, ValidInventory("SRV01", second));

        var installed = await InstalledAsync(credentials.ClientId);
        Assert.Equal([second.UpdateId], installed);
    }

    [Fact]
    public async Task InventoryWithoutUpdateList_KeepsPreviousUpdates()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        var update = Update();
        await SendAsync(client, credentials, ValidInventory("SRV01", update));

        await SendAsync(client, credentials, ValidInventory("SRV01") with { InstalledUpdates = null });

        Assert.Equal([update.UpdateId], await InstalledAsync(credentials.ClientId));
    }

    [Fact]
    public async Task SameUpdateFromTwoClients_IsOneCatalogEntry()
    {
        using var client = factory.CreateClient();
        var first = await RegisterTests.RegisterAsync(client);
        var second = await RegisterTests.RegisterAsync(client);
        var update = Update();

        await SendAsync(client, first, ValidInventory("SRV01", update));
        await SendAsync(client, second, ValidInventory("SRV02", update with { Title = "Other title" }));

        var catalog = await factory.WithDbAsync(db => db.Updates.Where(u => u.UpdateId == update.UpdateId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(catalog);
    }

    [Fact]
    public async Task RevisionsOfOneUpdate_AreDistinct()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        var id = Guid.NewGuid();

        await SendAsync(client, credentials, ValidInventory("SRV01", Update(id, 1), Update(id, 2)));

        Assert.Equal(2, (await InstalledAsync(credentials.ClientId)).Count);
    }

    public static TheoryData<int> InvalidInventoryCases() => [.. Enumerable.Range(0, InvalidInventories.Length)];

    private static readonly InventoryRequest[] InvalidInventories =
    [
        ValidInventory() with { HostName = "bad_name" },
        ValidInventory() with { Roles = ["Role With Space"] },
        ValidInventory() with { Roles = [""] },
        ValidInventory() with { Roles = [.. Enumerable.Range(0, InventoryRequest.MaxRoles + 1).Select(i => $"Role{i}")] },
        ValidInventory() with { OperatingSystem = ValidInventory().OperatingSystem with { Version = "Windows" } },
        ValidInventory() with { OperatingSystem = ValidInventory().OperatingSystem with { ProductName = "Bad\u0007Name" } },
        ValidInventory() with { OperatingSystem = ValidInventory().OperatingSystem with { EditionId = "Server Standard" } },
        ValidInventory() with { UpdateSource = new UpdateSourceInfo { UsesWsus = true, WsusServer = "line\nbreak" } },
        ValidInventory("SRV01", Update() with { KbArticle = "KB5034129" }),
        ValidInventory("SRV01", Update() with { Title = "" }),
        ValidInventory("SRV01", Update() with { Title = new string('x', 513) }),
        ValidInventory("SRV01", Update() with { Title = "Bad\u0000Title" }),
        ValidInventory("SRV01", Update() with { Revision = -1 }),
        ValidInventory() with { InstalledUpdates = [.. Enumerable.Range(0, InventoryRequest.MaxInstalledUpdates + 1).Select(_ => Update())] },
    ];

    [Theory]
    [MemberData(nameof(InvalidInventoryCases))]
    public async Task InvalidInventory_IsRejected(int index)
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await SendAsync(client, credentials, InvalidInventories[index]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Inventory_WithoutCredentials_IsUnauthorized()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(InventoryUri, ValidInventory(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(RegistrationState.Rejected)]
    [InlineData(RegistrationState.Blocked)]
    [InlineData(RegistrationState.Deleted)]
    public async Task Inventory_InStateWithoutPermission_IsForbidden(RegistrationState state)
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, state), TestContext.Current.CancellationToken));

        using var response = await SendAsync(client, credentials, ValidInventory());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_WithOsVersion_UpdatesClient()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, HeartbeatUri)
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0", OsVersion = "10.0.20348.2800" }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal("10.0.20348.2800", stored.OsVersion);
    }

    [Fact]
    public async Task Heartbeat_WithInvalidOsVersion_IsRejected()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, HeartbeatUri)
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.0.0", OsVersion = "ten" }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<List<Guid>> InstalledAsync(Guid clientId) =>
        factory.WithDbAsync(db => db.ClientUpdates.Where(cu => cu.ClientId == clientId).Select(cu => cu.UpdateId).ToListAsync(TestContext.Current.CancellationToken));

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, RegisterResponse credentials, InventoryRequest inventory)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, InventoryUri) { Content = JsonContent.Create(inventory) };
        request.Headers.Authorization = AgentCredentials.ToHeader(credentials.ClientId, credentials.ClientSecret);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
