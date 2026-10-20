using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Contracts.Installation;
using SUSyphos.Contracts.Trust;
using SUSyphos.Server.Administration;
using SUSyphos.Server.AgentPackages;
using SUSyphos.Server.Data;
using SUSyphos.Server.Trust;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class AgentVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.2.3-alpha.0.4+abc", "1.2.3")]
    [InlineData("1.2.3-alpha", "1.2.3-alpha.1")]
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha.beta")]
    [InlineData("1.2.3-alpha.2", "1.2.3-alpha.10")]
    [InlineData("1.2.3-rc.1", "1.2.3")]
    public void Ordering_FollowsSemVer(string lower, string higher)
    {
        Assert.True(AgentVersion.TryParse(lower, out var a));
        Assert.True(AgentVersion.TryParse(higher, out var b));
        Assert.True(a < b);
        Assert.True(b > a);
    }

    [Fact]
    public void BuildMetadata_IsIgnored()
    {
        Assert.True(AgentVersion.TryParse("0.3.0+4f2a", out var version));
        Assert.Equal("0.3.0", version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3-<script>")]
    [InlineData("99999999999.0.0")]
    public void Invalid_IsRejected(string? text)
    {
        Assert.False(AgentVersion.TryParse(text, out _));
    }
}

public sealed class AgentReleaseSourceTests
{
    [Theory]
    [InlineData("https://github.com/debold/UpdateCoordinator/releases/download/release/v0.4.0/SUSyphos.Agent-0.4.0.msi", true)]
    [InlineData("http://github.com/debold/UpdateCoordinator/releases/download/release/v0.4.0/x.msi", false)]
    [InlineData("https://github.com:8443/debold/UpdateCoordinator/releases/download/x.msi", false)]
    [InlineData("https://evil.example/debold/UpdateCoordinator/releases/download/x.msi", false)]
    [InlineData("https://github.com/attacker/SUSyphos/releases/download/x.msi", false)]
    [InlineData("https://github.com.evil.example/debold/UpdateCoordinator/releases/download/x.msi", false)]
    public void OnlyAssetsOfTheRepository_AreAccepted(string url, bool expected)
    {
        Assert.Equal(expected, GitHubAgentReleases.IsRepositoryAsset(new Uri(url), "debold/UpdateCoordinator"));
    }

    [Fact]
    public void Checksum_IsReadForTheExactFileName()
    {
        var hash = new string('a', 64);
        var text = $"{new string('b', 64)}  SUSyphos.Server-0.4.0.msi\r\n{hash}  SUSyphos.Agent-0.4.0.msi\r\n";

        Assert.Equal(hash, AgentPackageAdministration.ParseChecksum(text, "SUSyphos.Agent-0.4.0.msi"));
        Assert.Null(AgentPackageAdministration.ParseChecksum(text, "SUSyphos.Agent-0.5.0.msi"));
        Assert.Null(AgentPackageAdministration.ParseChecksum("xyz  SUSyphos.Agent-0.4.0.msi", "SUSyphos.Agent-0.4.0.msi"));
    }
}

/// <summary>Release source with in-memory assets instead of GitHub.</summary>
internal sealed class FakeAgentReleaseSource : IAgentReleaseSource
{
    public List<AgentRelease> Releases { get; } = [];

    public Dictionary<Uri, byte[]> Assets { get; } = [];

    public int Downloads { get; private set; }

    public void Add(string version, byte[] package, string? checksum = null, bool prerelease = false)
    {
        Assert.True(AgentVersion.TryParse(version, out var parsed));
        var packageUrl = new Uri($"https://github.com/debold/UpdateCoordinator/releases/download/release/v{version}/SUSyphos.Agent-{version}.msi");
        var sumsUrl = new Uri($"https://github.com/debold/UpdateCoordinator/releases/download/release/v{version}/SHA256SUMS.txt");
        Assets[packageUrl] = package;
        Assets[sumsUrl] = System.Text.Encoding.UTF8.GetBytes($"{checksum ?? Convert.ToHexStringLower(SHA256.HashData(package))}  SUSyphos.Agent-{version}.msi\n");
        Releases.Add(new AgentRelease(parsed, packageUrl, sumsUrl, prerelease));
    }

    public Task<IReadOnlyList<AgentRelease>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentRelease>>(Releases);

    public Task<Stream> OpenAsync(Uri asset, CancellationToken cancellationToken)
    {
        Downloads++;
        return Task.FromResult<Stream>(new MemoryStream(Assets[asset]));
    }
}

internal sealed class AgentPackageServerFactory : ServerFactory
{
    public FakeAgentReleaseSource Releases { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services => services.AddSingleton<IAgentReleaseSource>(Releases));
    }
}

public sealed class AgentPackageTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private static readonly byte[] Package = [0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3, 4];
    private readonly AgentPackageServerFactory _factory = new();

    [Fact]
    public async Task Check_DownloadsNewerReleaseVerifiedByChecksum_AndNothingElse()
    {
        _factory.Releases.Add("1.3.0", Package);
        _factory.Releases.Add("1.1.0", Package);

        var first = await WithAsync<AgentPackageAdministration, AgentPackageCheck>(a => a.CheckAsync("admin", CancellationToken.None));
        var second = await WithAsync<AgentPackageAdministration, AgentPackageCheck>(a => a.CheckAsync(null, CancellationToken.None));

        Assert.Equal("1.3.0", first.AddedVersion);
        Assert.Null(second.AddedVersion);
        var stored = await _factory.WithDbAsync(db => db.AgentPackages.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Package)), stored.Sha256);
        Assert.Null(stored.ReleasedThroughRingOrder);
        Assert.True(File.Exists(Path.Combine(_factory.AgentPackageDirectory, "SUSyphos.Agent-1.3.0.msi")));
        Assert.True(await _factory.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "AgentPackage.Checked", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Check_RejectsPackageNotMatchingItsChecksum()
    {
        _factory.Releases.Add("1.3.0", Package, checksum: new string('0', 64));

        var result = await WithAsync<AgentPackageAdministration, AgentPackageCheck>(a => a.CheckAsync(null, CancellationToken.None));

        Assert.NotNull(result.Error);
        Assert.False(await _factory.WithDbAsync(db => db.AgentPackages.AnyAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(Directory.Exists(_factory.AgentPackageDirectory) ? Directory.GetFiles(_factory.AgentPackageDirectory) : []);
        Assert.NotNull(await WithAsync<AgentPackageAdministration, AgentPackageCheck?>(a => a.GetLastCheckAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Release_GoesRingByRing_AndHeartbeatOffersOnlyReleasedRings()
    {
        await using var scenario = new ScenarioClients(_factory);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        var production = await scenario.AddClientAsync("PROD01", RingEntity.ProductionId);
        await AddPackageAsync("1.3.0");

        Assert.Null((await HeartbeatAsync(scenario.Http, pilot)).AgentUpdate);

        Assert.True((await WithAsync<AgentPackageAdministration, Outcome>(a => a.ReleaseAsync("1.3.0", AgentPackageAdministration.ReleaseScope.NextRing, "admin", CancellationToken.None))).Succeeded);
        var update = (await HeartbeatAsync(scenario.Http, pilot)).AgentUpdate;
        Assert.NotNull(update);
        Assert.Equal("1.3.0", update.Version);
        Assert.Equal(Package.Length, update.Size);
        Assert.Null((await HeartbeatAsync(scenario.Http, production)).AgentUpdate);

        Assert.True((await WithAsync<AgentPackageAdministration, Outcome>(a => a.ReleaseAsync("1.3.0", AgentPackageAdministration.ReleaseScope.AllRings, "admin", CancellationToken.None))).Succeeded);
        Assert.NotNull((await HeartbeatAsync(scenario.Http, production)).AgentUpdate);
        Assert.Equal(AgentPackageEntity.AllRings, await _factory.WithDbAsync(db => db.AgentPackages.Select(p => p.ReleasedThroughRingOrder).SingleAsync(TestContext.Current.CancellationToken)));

        // An agent already on the version (or newer) gets nothing; never a downgrade.
        Assert.Null((await HeartbeatAsync(scenario.Http, pilot, "1.3.0")).AgentUpdate);
        Assert.Null((await HeartbeatAsync(scenario.Http, pilot, "2.0.0")).AgentUpdate);

        Assert.True((await WithAsync<AgentPackageAdministration, Outcome>(a => a.WithdrawAsync("1.3.0", "admin", CancellationToken.None))).Succeeded);
        Assert.Null((await HeartbeatAsync(scenario.Http, production)).AgentUpdate);
    }

    [Fact]
    public async Task FailedSelfUpdate_IsStoredShownAndNotified()
    {
        await using var scenario = new ScenarioClients(_factory);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await AddPackageAsync("1.3.0");

        await HeartbeatAsync(scenario.Http, pilot, error: "msiexec ended with exit code 1603.");

        Assert.Equal("msiexec ended with exit code 1603.", await _factory.WithDbAsync(db => db.Clients.Where(c => c.Id == pilot.ClientId).Select(c => c.AgentUpdateError).SingleAsync(TestContext.Current.CancellationToken)));
        var events = await WithAsync<Notifications.NotificationEvents, List<Notifications.NotificationEvent>>(e => e.CollectAsync(CancellationToken.None));
        Assert.Contains(events, e => e.Kind == Notifications.NotificationKind.AgentUpdateFailed && e.Text.Contains("PILOT01", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == Notifications.NotificationKind.AgentVersionAvailable && e.Key == "agent.available:1.3.0");

        using var browser = await LoginAsync(AdminRoles.Operator);
        using (var page = await browser.GetAsync("/agents"))
        {
            Assert.Contains("exit code 1603", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        // A successful attempt (no error in the heartbeat) clears it.
        await HeartbeatAsync(scenario.Http, pilot);
        Assert.Null(await _factory.WithDbAsync(db => db.Clients.Where(c => c.Id == pilot.ClientId).Select(c => c.AgentUpdateError).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("bell\u0007")]
    public async Task Heartbeat_WithInvalidUpdateError_IsRejected(string error)
    {
        await using var scenario = new ScenarioClients(_factory);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.2.3", AgentUpdateError = error }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(pilot.ClientId, pilot.ClientSecret);

        using var response = await scenario.Http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Release_OfUnknownVersion_IsRefused()
    {
        var outcome = await WithAsync<AgentPackageAdministration, Outcome>(a => a.ReleaseAsync("9.9.9", AgentPackageAdministration.ReleaseScope.AllRings, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task PackageEndpoint_ServesOnlyTheTargetVersion()
    {
        await using var scenario = new ScenarioClients(_factory);
        var pilot = await scenario.AddClientAsync("PILOT01", RingEntity.PilotId);
        await AddPackageAsync("1.3.0");
        await AddPackageAsync("1.4.0");

        Assert.Equal(HttpStatusCode.NotFound, (await GetPackageAsync(scenario.Http, pilot, "1.3.0")).StatusCode);

        await WithAsync<AgentPackageAdministration, Outcome>(a => a.ReleaseAsync("1.3.0", AgentPackageAdministration.ReleaseScope.AllRings, "admin", CancellationToken.None));
        using (var served = await GetPackageAsync(scenario.Http, pilot, "1.3.0"))
        {
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal(Package, await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }

        // Not released, path tricks and unauthenticated requests get nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await GetPackageAsync(scenario.Http, pilot, "1.4.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetPackageAsync(scenario.Http, pilot, "..%2F..%2Fserver.db")).StatusCode);
        using var anonymous = await scenario.Http.GetAsync(new Uri(ApiRoutes.AgentPackageFor("1.3.0"), UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task PackageEndpoint_IsRefusedBeforeApproval()
    {
        using var http = _factory.CreateClient();
        var pending = await RegisterTests.RegisterAsync(http);
        await AddPackageAsync("1.3.0");
        await WithAsync<AgentPackageAdministration, Outcome>(a => a.ReleaseAsync("1.3.0", AgentPackageAdministration.ReleaseScope.AllRings, "admin", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, (await GetPackageAsync(http, pending, "1.3.0")).StatusCode);
    }

    [Fact]
    public async Task BundledPackage_IsRegisteredOnce()
    {
        var bundled = Path.Combine(Path.GetDirectoryName(_factory.AgentPackageDirectory)!, "bundled");
        Directory.CreateDirectory(bundled);
        await File.WriteAllBytesAsync(Path.Combine(bundled, "SUSyphos.Agent-1.3.0.msi"), Package, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(bundled, "SUSyphos.Agent-not-a-version.msi"), Package, TestContext.Current.CancellationToken);

        // The start-up registration may run at the same time; two parallel registrations must both succeed.
        await Task.WhenAll(
            WithAsync<AgentPackageStore, bool>(async s => { await s.RegisterBundledAsync(CancellationToken.None); return true; }),
            WithAsync<AgentPackageStore, bool>(async s => { await s.RegisterBundledAsync(CancellationToken.None); return true; }));
        await WithAsync<AgentPackageStore, bool>(async s => { await s.RegisterBundledAsync(CancellationToken.None); return true; });

        var package = await _factory.WithDbAsync(db => db.AgentPackages.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("1.3.0", package.Version);
        Assert.Equal(AgentPackageSource.Bundled, package.Source);
    }

    [Fact]
    public async Task InstallerDownload_CarriesAddressAndAnchorFingerprint()
    {
        await AddPackageAsync("1.3.0");
        using var browser = await LoginAsync(AdminRoles.Operator);

        using var response = await browser.GetAsync($"{AgentInstallerEndpoints.Route}?address={Uri.EscapeDataString("https://uc01.lab.local:8443/")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fingerprint = await WithAsync<TrustAnchor, string>(anchor => Task.FromResult(TrustBindingFormat.ComputeAnchorFingerprint(anchor.PublicKey)));
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        Assert.Equal($"SUSyphos.Agent-1.3.0_uc01.lab.local_8443_{fingerprint}.msi", fileName?.Trim('"'));
        Assert.True(AgentInstallerFileName.TryParse(fileName?.Trim('"'), out var url, out var parsed));
        Assert.Equal("https://uc01.lab.local:8443/", url);
        Assert.Equal(fingerprint, parsed);
        Assert.Equal(Package, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://uc01.lab.local/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pw@uc01/")]
    public async Task InstallerDownload_RejectsInvalidAddresses(string address)
    {
        await AddPackageAsync("1.3.0");
        using var browser = await LoginAsync(AdminRoles.Operator);

        using var response = await browser.GetAsync($"{AgentInstallerEndpoints.Route}?address={Uri.EscapeDataString(address)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InstallerDownload_IsRefusedForReaders()
    {
        await AddPackageAsync("1.3.0");
        using var browser = await LoginAsync(AdminRoles.Reader);

        using var response = await browser.GetAsync($"{AgentInstallerEndpoints.Route}?address={Uri.EscapeDataString("https://uc01/")}");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Page_ShowsVersions_AndOnlyAdministratorsRelease()
    {
        await AddPackageAsync("1.3.0");
        using var operatorBrowser = await LoginAsync(AdminRoles.Operator);

        using (var page = await operatorBrowser.GetAsync("/agents"))
        {
            var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("1.3.0", html, StringComparison.Ordinal);
            Assert.Contains("Download installer", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Release to all rings now", html, StringComparison.Ordinal);
        }

        using (await operatorBrowser.PostFormAsync("/agents", new Dictionary<string, string> { ["_handler"] = "agents-release", ["Action"] = "all:1.3.0" }))
        {
        }

        Assert.Null(await _factory.WithDbAsync(db => db.AgentPackages.Select(p => p.ReleasedThroughRingOrder).SingleAsync(TestContext.Current.CancellationToken)));

        await _factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(_factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        using (var released = await admin.PostFormAsync("/agents", new Dictionary<string, string> { ["_handler"] = "agents-release", ["Action"] = "all:1.3.0" }))
        {
            Assert.Contains("was released", await released.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(AgentPackageEntity.AllRings, await _factory.WithDbAsync(db => db.AgentPackages.Select(p => p.ReleasedThroughRingOrder).SingleAsync(TestContext.Current.CancellationToken)));

        using (var bad = await admin.PostFormAsync("/agents", new Dictionary<string, string> { ["_handler"] = "agents-release", ["Action"] = "nonsense" }))
        {
            Assert.Contains("Unknown action.", await bad.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task AddPackageAsync(string version)
    {
        Assert.True(AgentVersion.TryParse(version, out var parsed));
        Assert.True(await WithAsync<AgentPackageStore, bool>(s => s.AddAsync(parsed, AgentPackageSource.GitHub, new MemoryStream(Package), null, CancellationToken.None)));
    }

    private async Task<DashboardClient> LoginAsync(string role)
    {
        await _factory.CreateUserAsync("user", Password, role);
        var browser = new DashboardClient(_factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        return browser;
    }

    private async Task<TResult> WithAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        _factory.StartServer();
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static async Task<HeartbeatResponse> HeartbeatAsync(HttpClient http, RegisterResponse client, string version = "1.2.3", string? error = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = version, AgentUpdateError = error }),
        };
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<HttpResponseMessage> GetPackageAsync(HttpClient http, RegisterResponse client, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/agents/package/{version}", UriKind.Relative));
        request.Headers.Authorization = AgentCredentials.ToHeader(client.ClientId, client.ClientSecret);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Approved agents in rings on the given factory.</summary>
    private sealed class ScenarioClients(ServerFactory factory) : IAsyncDisposable
    {
        public HttpClient Http { get; } = factory.CreateClient();

        public async Task<RegisterResponse> AddClientAsync(string host, int ring)
        {
            var credentials = await RegisterTests.RegisterAsync(Http);
            await factory.WithDbAsync(db => db.Clients.Where(c => c.Id == credentials.ClientId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved).SetProperty(c => c.HostName, host).SetProperty(c => c.RingId, ring), TestContext.Current.CancellationToken));
            return credentials;
        }

        public ValueTask DisposeAsync()
        {
            Http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
