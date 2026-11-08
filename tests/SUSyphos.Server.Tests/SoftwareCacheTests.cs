using System.Net;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Amsi;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>The installer cache and the WinGet source of the central server: download at rollout start, scan, then offer.</summary>
public sealed class SoftwareCacheTests : IAsyncDisposable
{
    private readonly ServerFactory _factory = new();
    private readonly FakeSoftwareSource _source = new(Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", "cache-" + Guid.NewGuid().ToString("N")));
    private readonly FakeScanner _scanner = new();

    public SoftwareCacheTests() => _source.SetIndex(new TestIndexPackage("7zip.7zip", "7-Zip", "26.06"));

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _source.Dispose();
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<int> CacheAsync(ScanMode mode = ScanMode.Enforce) => InScopeAsync(services => new SoftwareCacheService(
        services.GetRequiredService<ServerDbContext>(),
        _source,
        _scanner,
        services.GetRequiredService<SoftwareSourceCache>(),
        services.GetRequiredService<AuditLog>(),
        Options.Create(new SoftwareOptions { Directory = services.GetRequiredService<IOptions<SoftwareOptions>>().Value.Directory, ScanMode = mode }),
        TimeProvider.System,
        NullLogger<SoftwareCacheService>.Instance).CacheReleasedAsync(CancellationToken.None));

    private Task<bool> AddPackageAsync() => InScopeAsync(async services =>
    {
        var catalog = new SoftwareCatalog(services.GetRequiredService<ServerDbContext>(), _source, services.GetRequiredService<AuditLog>(), Options.Create(new SoftwareOptions { CheckEnabled = false }), TimeProvider.System, NullLogger<SoftwareCatalog>.Instance);
        Assert.True((await catalog.AddPackageAsync("7zip.7zip", "admin", CancellationToken.None)).Succeeded);
        return true;
    });

    private Task<bool> StartRolloutAsync(string version, RolloutState state = RolloutState.Active) => _factory.WithDbAsync(async db =>
    {
        db.Rollouts.Add(new RolloutEntity { UpdateId = SoftwarePackages.SoftwareUpdateId("7zip.7zip", version), Revision = 1, State = state, StartedAtUtc = DateTime.UtcNow, StartedBy = "admin" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return true;
    });

    private Task<PackageVersionEntity> VersionAsync(string version = "26.06") =>
        _factory.WithDbAsync(db => db.PackageVersions.AsNoTracking().SingleAsync(v => v.Version == version, TestContext.Current.CancellationToken));

    [Fact]
    public async Task VersionWithoutRollout_IsNotDownloaded()
    {
        await AddPackageAsync();

        Assert.Equal(0, await CacheAsync());
        Assert.Equal(0, _source.Downloads);
    }

    [Fact]
    public async Task StartedVersion_IsScanned_Offered_AndServedAnonymously()
    {
        await AddPackageAsync();
        await StartRolloutAsync("26.06");

        Assert.Equal(1, await CacheAsync());

        var version = await VersionAsync();
        Assert.Equal(InstallerScanState.Clean, version.ScanState);
        Assert.NotNull(version.CachedAtUtc);
        Assert.Equal(_source.InstallerContent.Length, version.SizeBytes);
        Assert.Single(_scanner.Scanned);

        using var http = _factory.CreateClient();
        var manifest = JsonNode.Parse(await http.GetStringAsync(new Uri("/winget/api/packageManifests/7zip.7zip", UriKind.Relative), TestContext.Current.CancellationToken))!;
        var url = manifest["Data"]!["Versions"]![0]!["Installers"]![0]!["InstallerUrl"]!.GetValue<string>();
        Assert.Contains("/winget/files/" + version.InstallerFile, url, StringComparison.Ordinal);
        var file = await http.GetByteArrayAsync(new Uri(new Uri(url).AbsolutePath, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(_source.InstallerContent, file);

        // Running again changes nothing.
        Assert.Equal(0, await CacheAsync());
        Assert.Equal(1, _source.Downloads);
    }

    [Theory]
    [InlineData("../server.db")]
    [InlineData("index.json")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000.msi")]
    public async Task Files_OnlyCachedNamesAreServed(string name)
    {
        using var http = _factory.CreateClient();

        using var response = await http.GetAsync(new Uri("/winget/files/" + Uri.EscapeDataString(name), UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detection_KeepsTheVersionOut_DeletesItsFiles_AndIsAudited()
    {
        await AddPackageAsync();
        await StartRolloutAsync("26.06");
        _scanner.Result = new ScanResult(ScanOutcome.Detected, "The antivirus product reported a threat.", 32768);

        Assert.Equal(0, await CacheAsync());

        var version = await VersionAsync();
        Assert.Equal(InstallerScanState.Detected, version.ScanState);
        Assert.Null(version.CachedAtUtc);
        Assert.Contains("threat", version.ScanDetail, StringComparison.Ordinal);
        Assert.True(await _factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "Package.VirusDetected", TestContext.Current.CancellationToken)));
        var directory = await InScopeAsync(s => Task.FromResult(s.GetRequiredService<IOptions<SoftwareOptions>>().Value.Directory));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.msi", SearchOption.AllDirectories));

        // A detected version is not downloaded again.
        await CacheAsync();
        Assert.Equal(1, _source.Downloads);
    }

    [Theory]
    [InlineData(ScanOutcome.TooLarge, (int)ScanMode.Enforce, false, (int)InstallerScanState.Failed)]
    [InlineData(ScanOutcome.Failed, (int)ScanMode.Enforce, false, (int)InstallerScanState.Failed)]
    [InlineData(ScanOutcome.Failed, (int)ScanMode.Audit, true, (int)InstallerScanState.Failed)]
    [InlineData(ScanOutcome.Detected, (int)ScanMode.Audit, true, (int)InstallerScanState.Detected)]
    [InlineData(ScanOutcome.Unavailable, (int)ScanMode.Enforce, true, (int)InstallerScanState.NotScanned)]
    [InlineData(ScanOutcome.Detected, (int)ScanMode.Off, true, (int)InstallerScanState.NotScanned)]
    public async Task ScanResults_FollowTheMode(ScanOutcome outcome, int modeValue, bool offered, int stateValue)
    {
        var (mode, state) = ((ScanMode)modeValue, (InstallerScanState)stateValue);
        await AddPackageAsync();
        await StartRolloutAsync("26.06");
        _scanner.Result = new ScanResult(outcome, "detail");

        Assert.Equal(offered ? 1 : 0, await CacheAsync(mode));

        var version = await VersionAsync();
        Assert.Equal(state, version.ScanState);
        Assert.Equal(offered, version.CachedAtUtc is not null);
        Assert.Equal(mode == ScanMode.Off ? 0 : 1, _scanner.Scanned.Count);
    }

    [Fact]
    public async Task FailedDownload_IsRecorded_AndTriedAgain()
    {
        await AddPackageAsync();
        await StartRolloutAsync("26.06");
        _source.DownloadFailure = new HttpRequestException("SourceForge does not answer");

        Assert.Equal(0, await CacheAsync());
        Assert.Contains("SourceForge", (await VersionAsync()).ScanDetail, StringComparison.Ordinal);

        _source.DownloadFailure = null;
        Assert.Equal(1, await CacheAsync());
    }

    [Fact]
    public async Task Cache_KeepsTheNewestTwoVersions_AndThoseStillRollingOut()
    {
        await AddPackageAsync();
        await StartRolloutAsync("26.06");
        await CacheAsync();
        foreach (var next in new[] { "26.07", "26.08" })
        {
            _source.SetIndex(new TestIndexPackage("7zip.7zip", "7-Zip", next));
            _source.InstallerContent = System.Text.Encoding.UTF8.GetBytes("installer " + next);
            await InScopeAsync(async services =>
                await new SoftwareCatalog(services.GetRequiredService<ServerDbContext>(), _source, services.GetRequiredService<AuditLog>(), Options.Create(new SoftwareOptions { CheckEnabled = false }), TimeProvider.System, NullLogger<SoftwareCatalog>.Instance).CheckVersionsAsync(CancellationToken.None));
            await StartRolloutAsync(next);
        }

        await _factory.WithDbAsync(db => db.Rollouts.Where(r => r.UpdateId == SoftwarePackages.SoftwareUpdateId("7zip.7zip", "26.06")).ExecuteUpdateAsync(s => s.SetProperty(r => r.State, RolloutState.Stopped), TestContext.Current.CancellationToken));
        await CacheAsync();

        Assert.Null((await VersionAsync("26.06")).CachedAtUtc);
        Assert.NotNull((await VersionAsync("26.07")).CachedAtUtc);
        Assert.NotNull((await VersionAsync("26.08")).CachedAtUtc);
        using var http = _factory.CreateClient();
        var manifest = JsonNode.Parse(await http.GetStringAsync(new Uri("/winget/api/packageManifests/7zip.7zip", UriKind.Relative), TestContext.Current.CancellationToken))!;
        Assert.Equal(["26.07", "26.08"], manifest["Data"]!["Versions"]!.AsArray().Select(v => v!["PackageVersion"]!.GetValue<string>()).Order());
    }

    private sealed class FakeScanner : IInstallerScanner
    {
        public ScanResult Result { get; set; } = new(ScanOutcome.Clean, "No threat found.", 1);

        public List<string> Scanned { get; } = [];

        public ScanResult Scan(string path, long maxBytes)
        {
            Assert.True(File.Exists(path), "The scanner gets the downloaded file.");
            Scanned.Add(path);
            return Result;
        }
    }
}
