using System.Net;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The installed programs and the WinGet support state that agents send with the inventory (0.9).</summary>
public sealed class SoftwareInventoryTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    internal static InstalledSoftware Program(string key = "7-Zip", string? packageId = "7zip.7zip") => new()
    {
        View = SoftwareView.Machine64,
        KeyName = key,
        DisplayName = "7-Zip 26.06 (x64)",
        DisplayVersion = "26.06",
        Publisher = "Igor Pavlov",
        PackageId = packageId,
        InstalledVersion = packageId is null ? null : "26.06",
    };

    private static InventoryRequest WithSoftware(SoftwareSupportState state, string? detail, params InstalledSoftware[] software) =>
        InventoryTests.ValidInventory("SRV-SW") with { InstalledSoftware = software, Software = new SoftwareSupport { State = state, Detail = detail } };

    [Fact]
    public async Task InstalledSoftware_IsStoredWithTheSupportState()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await InventoryTests.SendAsync(client, credentials, WithSoftware(
            SoftwareSupportState.Supported, "ignored when supported", Program(), Program("{2E2A3B4C-0000-0000-0000-000000000001}", null) with { View = SoftwareView.Machine32, DisplayName = "Vendor Agent" }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await SoftwareAsync(credentials.ClientId);
        Assert.Equal(2, stored.Count);
        var zip = stored.Single(s => s.KeyName == "7-Zip");
        Assert.Equal("7zip.7zip", zip.PackageId);
        Assert.Equal("26.06", zip.InstalledVersion);
        Assert.Equal(SoftwareMatch.WinGet, zip.MatchKind);
        var vendor = stored.Single(s => s.View == SoftwareView.Machine32);
        Assert.Null(vendor.PackageId);
        Assert.Equal(SoftwareMatch.None, vendor.MatchKind);
        var server = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(SoftwareSupportState.Supported, server.SoftwareState);
        Assert.Null(server.SoftwareDetail);
    }

    [Fact]
    public async Task NewInventory_ReplacesTheSoftware_AndWithoutAListKeepsIt()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program("A"), Program("B")));

        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program("C")));
        Assert.Equal(["C"], (await SoftwareAsync(credentials.ClientId)).Select(s => s.KeyName));

        await InventoryTests.SendAsync(client, credentials, InventoryTests.ValidInventory("SRV-SW") with { InstalledSoftware = null, Software = new SoftwareSupport { State = SoftwareSupportState.Failed, Detail = "The WinGet helper of the agent did not finish in time." } });
        Assert.Equal(["C"], (await SoftwareAsync(credentials.ClientId)).Select(s => s.KeyName));
        var server = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(SoftwareSupportState.Failed, server.SoftwareState);
        Assert.Equal("The WinGet helper of the agent did not finish in time.", server.SoftwareDetail);
    }

    [Fact]
    public async Task OlderAgent_WithoutSoftware_KeepsTheLastState()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.NotSupported, "Server Core: WinGet needs the desktop experience.", Program()));

        await InventoryTests.SendAsync(client, credentials, InventoryTests.ValidInventory("SRV-SW"));

        var server = await factory.WithDbAsync(db => db.Clients.SingleAsync(c => c.Id == credentials.ClientId, TestContext.Current.CancellationToken));
        Assert.Equal(SoftwareSupportState.NotSupported, server.SoftwareState);
        Assert.Single(await SoftwareAsync(credentials.ClientId));
    }

    [Fact]
    public async Task CatalogMatch_SurvivesTheNextInventory_WithTheNewVersion()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program(packageId: null)));
        await MatchAsync(credentials.ClientId, SoftwareMatch.Catalog, "26.06");

        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program(packageId: null) with { DisplayVersion = "26.07" }));

        var zip = Assert.Single(await SoftwareAsync(credentials.ClientId));
        Assert.Equal(("7zip.7zip", "26.07", SoftwareMatch.Catalog), (zip.PackageId, zip.InstalledVersion, zip.MatchKind));
    }

    [Fact]
    public async Task WinGetMatch_SurvivesAFailedHelper_ButNotASupportedReportWithoutIt()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program()));

        // The helper failed: the agent sends the registry list only.
        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Failed, "The WinGet helper of the agent did not finish in time.", Program(packageId: null)));
        var zip = Assert.Single(await SoftwareAsync(credentials.ClientId));
        Assert.Equal(("7zip.7zip", "26.06", SoftwareMatch.WinGet), (zip.PackageId, zip.InstalledVersion, zip.MatchKind));

        // WinGet ran and no longer correlates the program: the match goes.
        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program(packageId: null)));
        zip = Assert.Single(await SoftwareAsync(credentials.ClientId));
        Assert.Equal((null, null, SoftwareMatch.None), (zip.PackageId, zip.InstalledVersion, zip.MatchKind));
    }

    [Fact]
    public async Task Match_IsNotCarriedToAnotherKey()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);
        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program(packageId: null)));
        await MatchAsync(credentials.ClientId, SoftwareMatch.Catalog, "26.06");

        await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program("Other", null)));

        Assert.Null(Assert.Single(await SoftwareAsync(credentials.ClientId)).PackageId);
    }

    private Task<int> MatchAsync(Guid clientId, SoftwareMatch kind, string version) => factory.WithDbAsync(db =>
        db.ClientSoftware.Where(s => s.ClientId == clientId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.PackageId, "7zip.7zip").SetProperty(e => e.InstalledVersion, version).SetProperty(e => e.MatchKind, kind), TestContext.Current.CancellationToken));

    [Fact]
    public async Task SameKeyTwice_IsStoredOnce()
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await InventoryTests.SendAsync(client, credentials, WithSoftware(SoftwareSupportState.Supported, null, Program("Same"), Program("SAME")));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await SoftwareAsync(credentials.ClientId));
    }

    public static TheoryData<int> InvalidCases() => [.. Enumerable.Range(0, Invalid.Length)];

    private static readonly InventoryRequest[] Invalid =
    [
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { PackageId = "no-dots" }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { PackageId = "7zip.7zip; drop table" }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { PackageId = "a." + new string('b', 127) }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { DisplayName = string.Empty }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { DisplayName = "Bad\u0000Name" }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { KeyName = new string('k', 257) }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { DisplayVersion = "1.0\n" }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { InstalledVersion = "<script>" }),
        WithSoftware(SoftwareSupportState.Supported, null, Program() with { Publisher = "Line\nBreak" }),
        WithSoftware(SoftwareSupportState.Failed, "Bad\u0007Detail", Program()),
        WithSoftware(SoftwareSupportState.Failed, new string('d', 257), Program()),
        InventoryTests.ValidInventory() with { InstalledSoftware = [.. Enumerable.Range(0, InstalledSoftware.MaxEntries + 1).Select(i => Program($"K{i}"))] },
    ];

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidSoftware_IsRejected(int index)
    {
        using var client = factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(client);

        using var response = await InventoryTests.SendAsync(client, credentials, Invalid[index]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await SoftwareAsync(credentials.ClientId));
    }

    private Task<List<ClientSoftwareEntity>> SoftwareAsync(Guid clientId) =>
        factory.WithDbAsync(db => db.ClientSoftware.AsNoTracking().Where(s => s.ClientId == clientId).ToListAsync(TestContext.Current.CancellationToken));
}
