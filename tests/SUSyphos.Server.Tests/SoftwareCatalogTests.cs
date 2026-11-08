using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;
using SUSyphos.WinGet.Catalog;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Tests;

/// <summary>The software catalog of the central server: packages from the inventory or an administrator, new versions from the index.</summary>
public sealed class SoftwareCatalogTests : IAsyncDisposable
{
    private const string ZipCode = "{23170F69-40C1-2702-2600-000001000000}";

    private readonly ServerFactory _factory = new();
    private readonly FakeSource _source;

    public SoftwareCatalogTests()
    {
        _source = new FakeSource(Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", "catalog-" + Guid.NewGuid().ToString("N")));
        _source.SetIndex(
            new TestIndexPackage("7zip.7zip", "7-Zip", "26.06", "7zip", ProductCodes: [ZipCode]),
            new TestIndexPackage("Vendor.Shared1", "Shared One", "1.0", ProductCodes: ["{11111111-1111-1111-1111-111111111111}"]),
            new TestIndexPackage("Vendor.Shared2", "Shared Two", "1.0", ProductCodes: ["{11111111-1111-1111-1111-111111111111}"]),
            new TestIndexPackage("Vendor.Licensed", "Licensed Tool", "3.1"));
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _source.Dispose();
    }

    private async Task<T> WithCatalogAsync<T>(Func<SoftwareCatalog, Task<T>> action)
    {
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var catalog = new SoftwareCatalog(
            services.GetRequiredService<ServerDbContext>(),
            _source,
            services.GetRequiredService<AuditLog>(),
            Options.Create(new SoftwareOptions { CheckEnabled = false }),
            TimeProvider.System,
            NullLogger<SoftwareCatalog>.Instance);
        return await action(catalog);
    }

    private async Task<Guid> AddServerWithSoftwareAsync(params ClientSoftwareEntity[] programs)
    {
        using var http = _factory.CreateClient();
        var credentials = await RegisterTests.RegisterAsync(http);
        await _factory.WithDbAsync(async db =>
        {
            foreach (var program in programs)
            {
                program.ClientId = credentials.ClientId;
                db.ClientSoftware.Add(program);
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        return credentials.ClientId;
    }

    private static ClientSoftwareEntity Program(string key, string? packageId = null, string? version = "26.06") => new()
    {
        View = SoftwareView.Machine64,
        KeyName = key,
        DisplayName = key,
        DisplayVersion = version,
        PackageId = packageId,
        InstalledVersion = packageId is null ? null : version,
        MatchKind = packageId is null ? SoftwareMatch.None : SoftwareMatch.WinGet,
        ReportedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task PackageInstalledSomewhere_IsAdded_WithItsNewestVersionAsSoftwareUpdate()
    {
        await AddServerWithSoftwareAsync(Program("7-Zip", "7zip.7zip"));

        var (matched, added, failures) = await WithCatalogAsync(c => c.MatchInventoryAsync(CancellationToken.None));

        Assert.Equal((0, 1, 0), (matched, added, failures));
        var package = await _factory.WithDbAsync(db => db.Packages.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("7zip.7zip", "7-Zip", "Igor Pavlov", "inventory"), (package.PackageId, package.Name, package.Publisher, package.AddedBy));
        Assert.Equal("https://www.7-zip.org/", package.PackageUrl);
        Assert.Null(package.LicenseUrl);
        Assert.False(package.HasAgreements);
        var version = await _factory.WithDbAsync(db => db.PackageVersions.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06"), version.UpdateId);
        Assert.Equal(new DateOnly(2026, 11, 1), version.ReleaseDate);
        Assert.Contains("\"InstallerSha256\"", version.Manifest, StringComparison.Ordinal);
        var update = await _factory.WithDbAsync(db => db.Updates.SingleAsync(u => u.UpdateId == version.UpdateId, TestContext.Current.CancellationToken));
        Assert.Equal((UpdateKind.Software, "7zip.7zip", "26.06", "7-Zip 26.06", 1), (update.Kind, update.PackageId, update.PackageVersion, update.Title, update.Revision));
        Assert.True(await _factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Package.Added" && a.Target == "7zip.7zip", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ProgramWithAKnownProductCode_IsMatched_AmbiguousCodesAreNot()
    {
        var client = await AddServerWithSoftwareAsync(Program(ZipCode.ToLowerInvariant()), Program("{11111111-1111-1111-1111-111111111111}"), Program("NotAProductCode"));

        var (matched, added, _) = await WithCatalogAsync(c => c.MatchInventoryAsync(CancellationToken.None));

        Assert.Equal((1, 1), (matched, added));
        var programs = await _factory.WithDbAsync(db => db.ClientSoftware.Where(s => s.ClientId == client).ToListAsync(TestContext.Current.CancellationToken));
        var zip = programs.Single(p => p.PackageId != null);
        Assert.Equal(("7zip.7zip", "26.06", SoftwareMatch.Catalog), (zip.PackageId, zip.InstalledVersion, zip.MatchKind));
        Assert.Equal(2, programs.Count(p => p.PackageId is null));
    }

    [Fact]
    public async Task NewVersionInTheIndex_IsAdded_Once()
    {
        Assert.True((await WithCatalogAsync(c => c.AddPackageAsync("7zip.7zip", "admin", CancellationToken.None))).Succeeded);
        _source.SetIndex(new TestIndexPackage("7zip.7zip", "7-Zip", "26.07"));

        var first = await WithCatalogAsync(c => c.CheckVersionsAsync(CancellationToken.None));
        var second = await WithCatalogAsync(c => c.CheckVersionsAsync(CancellationToken.None));

        Assert.Equal((1, 0), first);
        Assert.Equal((0, 0), second);
        Assert.Equal(["26.06", "26.07"], await _factory.WithDbAsync(db => db.PackageVersions.Select(v => v.Version).OrderBy(v => v).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Updates.CountAsync(u => u.Kind == UpdateKind.Software, TestContext.Current.CancellationToken)));
        Assert.NotNull(await _factory.WithDbAsync(db => db.Packages.Select(p => p.CheckedAtUtc).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task PackageWithAgreements_IsMarked_AndANewAgreementNeedsANewAcceptance()
    {
        Assert.True((await WithCatalogAsync(c => c.AddPackageAsync("Vendor.Licensed", "admin", CancellationToken.None))).Succeeded);
        Assert.True(await _factory.WithDbAsync(db => db.Packages.Select(p => p.HasAgreements).SingleAsync(TestContext.Current.CancellationToken)));

        var package = new PackageEntity { PackageId = "A.B", Name = "A", AddedBy = "admin", AgreementsAcceptedAtUtc = DateTime.UtcNow, AgreementsAcceptedBy = "admin" };
        SoftwareCatalog.Describe(package, new JsonObject { ["PackageName"] = "A", ["Agreements"] = new JsonArray(new JsonObject { ["Agreement"] = "terms" }) });

        Assert.True(package.HasAgreements);
        Assert.Null(package.AgreementsAcceptedAtUtc);
        Assert.Null(package.AgreementsAcceptedBy);
    }

    [Fact]
    public void Describe_KeepsOnlyCleanTextAndHttpsLinks()
    {
        var package = new PackageEntity { PackageId = "A.B", Name = "Old", AddedBy = "admin" };

        SoftwareCatalog.Describe(package, new JsonObject
        {
            ["PackageName"] = "New\u0000Name",
            ["Publisher"] = new string('p', 300),
            ["Description"] = "Line one\nLine two\u0007",
            ["PackageUrl"] = "javascript:alert(1)",
            ["PublisherUrl"] = "http://example.com/",
            ["LicenseUrl"] = "https://example.com/license",
            ["Tags"] = new JsonArray("a", "b"),
        });

        Assert.Equal("NewName", package.Name);
        Assert.Equal(PackageEntity.MaxNameLength, package.Publisher!.Length);
        Assert.Equal("Line one\nLine two", package.Description);
        Assert.Null(package.PackageUrl);
        Assert.Null(package.PublisherUrl);
        Assert.Equal("https://example.com/license", package.LicenseUrl);
        Assert.Equal("a, b", package.Tags);
    }

    public static readonly TheoryData<string?, string> InvalidAdds = new()
    {
        { null, "WinGet ID" },
        { "nodots", "WinGet ID" },
        { "a.b'); DROP TABLE Packages;--", "WinGet ID" },
        { "Unknown.Package", "has no package" },
        { "Vendor.Shared1", "could not be read" },
    };

    [Theory]
    [MemberData(nameof(InvalidAdds))]
    public async Task AddingAnInvalidOrUnknownPackage_IsRefused(string? packageId, string error)
    {
        var outcome = await WithCatalogAsync(c => c.AddPackageAsync(packageId, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Contains(error, outcome.Error, StringComparison.Ordinal);
        Assert.False(await _factory.WithDbAsync(db => db.Packages.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.False(await _factory.WithDbAsync(db => db.Updates.AnyAsync(u => u.Kind == UpdateKind.Software, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task WithoutIndex_NothingIsAdded()
    {
        _source.ClearIndex();
        await AddServerWithSoftwareAsync(Program("7-Zip", "7zip.7zip"));

        var outcome = await WithCatalogAsync(c => c.AddPackageAsync("7zip.7zip", "admin", CancellationToken.None));
        var versions = await WithCatalogAsync(c => c.CheckVersionsAsync(CancellationToken.None));

        Assert.Contains("not been loaded", outcome.Error, StringComparison.Ordinal);
        Assert.Equal((0, 0), versions);
    }

    [Fact]
    public async Task SoftwareVersions_AreNeverArchivedLikeWindowsUpdates()
    {
        Assert.True((await WithCatalogAsync(c => c.AddPackageAsync("7zip.7zip", "admin", CancellationToken.None))).Succeeded);
        await _factory.WithDbAsync(db => db.Updates.ExecuteUpdateAsync(s => s.SetProperty(u => u.FirstSeenAtUtc, DateTime.UtcNow.AddYears(-1)), TestContext.Current.CancellationToken));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UpdateArchive>().RunAsync(CancellationToken.None);
        }

        Assert.Null(await _factory.WithDbAsync(db => db.Updates.Where(u => u.Kind == UpdateKind.Software).Select(u => u.ArchivedAtUtc).SingleAsync(TestContext.Current.CancellationToken)));
    }

    /// <summary>The community source with a test index and canned manifests; Vendor.Shared1 has no readable manifest.</summary>
    private sealed class FakeSource(string directory) : ISoftwareSource, IDisposable
    {
        public CommunityIndex? Index { get; private set; }

        public void SetIndex(params TestIndexPackage[] packages) =>
            Index = CommunityIndex.Open(TestCommunityIndex.Create(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db"), packages));

        public void ClearIndex() => Index = null;

        public Task<CommunityIndex> RefreshIndexAsync(CancellationToken cancellationToken) => Task.FromResult(Index!);

        public Task<CachedVersion> ReadManifestAsync(string packageId, string version, CancellationToken cancellationToken)
        {
            if (packageId == "Vendor.Shared1")
            {
                throw new HttpRequestException("404");
            }

            var locale = new JsonObject
            {
                ["PackageLocale"] = "en-US",
                ["PackageName"] = packageId == "7zip.7zip" ? "7-Zip" : "Licensed Tool",
                ["Publisher"] = packageId == "7zip.7zip" ? "Igor Pavlov" : "Vendor",
                ["PackageUrl"] = "https://www.7-zip.org/",
                ["LicenseUrl"] = "http://insecure.example/license",
                ["ShortDescription"] = "File archiver",
            };
            if (packageId == "Vendor.Licensed")
            {
                locale["Agreements"] = new JsonArray(new JsonObject { ["AgreementLabel"] = "EULA", ["Agreement"] = "terms" });
            }

            JsonObject[] installers = [new JsonObject { ["Architecture"] = "x64", ["InstallerType"] = "msi", ["InstallerUrl"] = "https://example.com/setup.msi", ["InstallerSha256"] = new string('A', 64), ["ReleaseDate"] = "2026-11-01" }];
            return Task.FromResult(new CachedVersion(packageId, version, locale, installers));
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
