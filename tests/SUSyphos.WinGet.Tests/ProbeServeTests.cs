using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SUSyphos.WinGet.RestSource;
using SUSyphos.WinGetProbe;

namespace SUSyphos.WinGet.Tests;

/// <summary>The routes of serve on a real Kestrel on loopback (plain HTTP here; the probe itself serves HTTPS only).</summary>
public sealed class ProbeServeTests : IAsyncLifetime
{
    private const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";
    private readonly string _folder = Directory.CreateTempSubdirectory("susyphos-serve-").FullName;
    private WebApplication? _app;
    private HttpClient? _http;

    public async ValueTask InitializeAsync()
    {
        var cache = PackageCache.Open(_folder, [], verifyFiles: false);
        Directory.CreateDirectory(cache.FilesFolder);
        await File.WriteAllTextAsync(Path.Combine(cache.FilesFolder, Hash + ".msi"), "installer", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_folder, "secret.txt"), "secret", TestContext.Current.CancellationToken);
        cache.Put(new CachedVersion("Vendor.Tool", "1.0", new JsonObject { ["PackageName"] = "Tool" }, [new JsonObject { ["InstallerSha256"] = Hash, ["CachedFile"] = Hash + ".msi" }]));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        ProbeServe.Map(_app, new RestSourceResponder(cache, new Uri("https://central:7443/files/")), cache, new ProbeOutput(Path.Combine(_folder, "results"), "SRV", TimeProvider.System));
        await _app.StartAsync(TestContext.Current.CancellationToken);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Routes_AnswerTheWinGetProtocol()
    {
        var token = TestContext.Current.CancellationToken;

        var information = await _http!.GetStringAsync(new Uri("/api/information", UriKind.Relative), token);
        Assert.Contains("\"SourceIdentifier\":\"susyphos\"", information, StringComparison.Ordinal);

        using var search = await _http.PostAsync(new Uri("/api/manifestSearch", UriKind.Relative), Json("""{"Query":{"KeyWord":"tool"}}"""), token);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal("application/json", search.Content.Headers.ContentType?.MediaType);

        using var none = await _http.PostAsync(new Uri("/api/manifestSearch", UriKind.Relative), Json("""{"Query":{"KeyWord":"other"}}"""), token);
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);

        var manifest = await _http.GetStringAsync(new Uri("/api/packageManifests/Vendor.Tool?version=1.0", UriKind.Relative), token);
        Assert.Contains($"https://central:7443/files/{Hash}.msi", manifest, StringComparison.Ordinal);

        Assert.Equal("installer", await _http.GetStringAsync(new Uri($"/files/{Hash}.msi", UriKind.Relative), token));
    }

    [Theory]
    [InlineData("/files/secret.txt")]
    [InlineData("/files/..%2Fsecret.txt")]
    [InlineData("/files/..%5Csecret.txt")]
    [InlineData("/files/2222222222222222222222222222222222222222222222222222222222222222.msi")]
    [InlineData("/api/packageManifests/Vendor.Other")]
    [InlineData("/index.json")]
    public async Task Routes_ServeNothingOutsideTheCache(string path)
    {
        using var response = await _http!.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"MaximumResults":"many"}""")]
    [InlineData("""{"Query":{"KeyWord":42}}""")]
    [InlineData("""{"Filters":[{"PackageMatchField":7,"RequestMatch":{"KeyWord":"x"}}]}""")]
    public async Task Search_RejectsBodiesItCannotRead(string body)
    {
        using var response = await _http!.PostAsync(new Uri("/api/manifestSearch", UriKind.Relative), Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_RejectsLargeBodies()
    {
        using var response = await _http!.PostAsync(new Uri("/api/manifestSearch", UriKind.Relative), Json("{\"Query\":{\"KeyWord\":\"" + new string('a', 100_000) + "\"}}"), TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode);
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
}
